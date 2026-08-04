import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs/promises";
import path from "node:path";

export const MAINTENANCE_FS_PROTOCOL = "codexpro-maintenance-fs-v1";
export const MAINTENANCE_FS_LIMITS = Object.freeze({
  maxDepth: 64,
  maxEntries: 4096,
  maxObservedBytes: 4 * 1024 * 1024 * 1024,
  maxResponseBytes: 4 * 1024 * 1024,
  maxDurationMs: 5000,
  maxHashBytes: 64 * 1024 * 1024,
  maxTextBytes: 1024 * 1024,
});

const REQUEST_LIMIT_BYTES = 8 * 1024;
const RESPONSE_LIMIT_BYTES = MAINTENANCE_FS_LIMITS.maxResponseBytes;
const STDERR_LIMIT_BYTES = 8 * 1024;
const REQUEST_TIMEOUT_MS = 6_000;
const RESPONSE_SIZE = Symbol("maintenanceFsResponseSize");
const STATUS = new Set(["ok", "invalid_request", "not_bound", "already_bound", "unsupported", "unavailable", "changed", "budget_exhausted", "invalid_entry", "too_large", "not_text"]);
const LIMITATION = new Set(["none", "depth", "entries", "observed_bytes", "response_bytes", "duration"]);
const KIND = new Set(["file", "directory", "reparse", "other"]);

export interface MaintenanceWalkBudgets {
  maxDepth: number;
  maxEntries: number;
  maxObservedBytes: number;
  maxResponseBytes: number;
  maxDurationMs: number;
}

export interface MaintenanceFsEntry {
  entryId: string;
  relativePath: string;
  kind: "file" | "directory" | "reparse" | "other";
  byteSize: number;
  modifiedUtc: string;
  attributes: string;
}

export interface MaintenanceWalkResult {
  status: "ok" | "budget_exhausted" | "invalid_request" | "not_bound" | "unavailable";
  complete: boolean;
  limitation: "none" | "depth" | "entries" | "observed_bytes" | "response_bytes" | "duration";
  returnedEntries: number;
  observedFileBytes: number;
  entries: MaintenanceFsEntry[];
}

export interface MaintenanceFileResult {
  status: "ok" | "invalid_request" | "not_bound" | "unavailable" | "changed" | "invalid_entry" | "too_large" | "not_text";
  entryId: string;
  byteCount: number;
  sha256: string | null;
  contentBase64: string | null;
}

export interface WindowsMaintenanceFsBoundaryOptions {
  executablePath: string;
  expectedSha256: string;
  expectedProtocol: typeof MAINTENANCE_FS_PROTOCOL;
  rootPath: string;
  defaultWalkBudgets?: Partial<MaintenanceWalkBudgets>;
}

export interface WindowsMaintenanceFsBoundary {
  ready(): Promise<void>;
  walk(budgets?: Partial<MaintenanceWalkBudgets>): Promise<MaintenanceWalkResult>;
  hashFile(entryId: string, maxBytes?: number): Promise<MaintenanceFileResult>;
  readTextFile(entryId: string, maxBytes?: number): Promise<MaintenanceFileResult>;
  close(): Promise<void>;
}

interface PendingResponse {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

export class ManagedWindowsMaintenanceFsBoundary implements WindowsMaintenanceFsBoundary {
  private readonly executablePath: string;
  private readonly expectedSha256: string;
  private readonly expectedProtocol: typeof MAINTENANCE_FS_PROTOCOL;
  private readonly rootPath: string;
  private readonly defaultWalkBudgets: Partial<MaintenanceWalkBudgets>;
  private readonly readyPromise: Promise<void>;
  private child?: ChildProcessWithoutNullStreams;
  private pending?: PendingResponse;
  private stdout = Buffer.alloc(0);
  private stderrBytes = 0;
  private failed = false;
  private closed = false;
  private sequence: Promise<unknown> = Promise.resolve();

  constructor(options: WindowsMaintenanceFsBoundaryOptions) {
    this.executablePath = resolveHelperExecutable(options.executablePath);
    this.expectedSha256 = String(options.expectedSha256);
    this.expectedProtocol = options.expectedProtocol;
    this.rootPath = String(options.rootPath);
    this.defaultWalkBudgets = Object.freeze({ ...(options.defaultWalkBudgets ?? {}) });
    this.readyPromise = this.start().catch((error: unknown) => {
      const failure = error instanceof Error ? error : new Error("maintenance filesystem helper unavailable");
      this.fail(failure);
      throw failure;
    });
    void this.readyPromise.catch(() => undefined);
  }

  async ready(): Promise<void> { await this.readyPromise; }

  walk(budgets: Partial<MaintenanceWalkBudgets> = {}): Promise<MaintenanceWalkResult> {
    const request = { ...MAINTENANCE_FS_LIMITS, ...this.defaultWalkBudgets, ...budgets };
    const selected: MaintenanceWalkBudgets = {
      maxDepth: request.maxDepth,
      maxEntries: request.maxEntries,
      maxObservedBytes: request.maxObservedBytes,
      maxResponseBytes: request.maxResponseBytes,
      maxDurationMs: request.maxDurationMs,
    };
    validateBudgets(selected);
    return this.request("walk", { ...selected }, (value) => validateWalk(value, selected));
  }

  hashFile(entryId: string, maxBytes = MAINTENANCE_FS_LIMITS.maxHashBytes): Promise<MaintenanceFileResult> {
    validateEntryId(entryId);
    validateBoundedInteger(maxBytes, 0, MAINTENANCE_FS_LIMITS.maxHashBytes);
    return this.request("hash_file", { entryId, maxBytes }, (value) => validateFile(value, "hash_file", entryId, maxBytes));
  }

  readTextFile(entryId: string, maxBytes = MAINTENANCE_FS_LIMITS.maxTextBytes): Promise<MaintenanceFileResult> {
    validateEntryId(entryId);
    validateBoundedInteger(maxBytes, 0, MAINTENANCE_FS_LIMITS.maxTextBytes);
    return this.request("read_text_file", { entryId, maxBytes }, (value) => validateFile(value, "read_text_file", entryId, maxBytes));
  }

  async close(): Promise<void> {
    if (this.closed) return;
    this.closed = true;
    try {
      await this.readyPromise;
      if (!this.failed) await this.enqueue({ protocol: MAINTENANCE_FS_PROTOCOL, operation: "close" }).then((value) => validateStatus(value, "close", new Set(["ok"])));
    } finally {
      this.fail(new Error("maintenance filesystem helper closed"));
    }
  }

  private async start(): Promise<void> {
    if (this.expectedProtocol !== MAINTENANCE_FS_PROTOCOL) throw new Error("maintenance filesystem helper protocol mismatch");
    if (process.platform !== "win32") throw new Error("maintenance filesystem helper unavailable");
    let executable: Buffer;
    try { executable = await fs.readFile(this.executablePath); }
    catch { throw new Error("maintenance filesystem helper unavailable"); }
    const actualHash = createHash("sha256").update(executable).digest("hex");
    if (!/^[a-f0-9]{64}$/.test(this.expectedSha256) || actualHash !== this.expectedSha256) throw new Error("maintenance filesystem helper fingerprint mismatch");

    const allowedEnvironment = ["SystemRoot", "WINDIR", "TEMP", "TMP"];
    const environment = Object.fromEntries(allowedEnvironment.flatMap((name) => process.env[name] ? [[name, process.env[name]!]] : []));
    const child = spawn(this.executablePath, ["--serve-maintenance-fs"], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true, shell: false, env: environment });
    this.child = child;
    child.stdout.on("data", (chunk: Buffer) => this.receive(chunk));
    child.stderr.on("data", (chunk: Buffer) => {
      this.stderrBytes += chunk.length;
      if (this.stderrBytes > STDERR_LIMIT_BYTES) this.fail(new Error("maintenance filesystem helper stderr limit exceeded"));
    });
    child.on("error", () => this.fail(new Error("maintenance filesystem helper failed to start")));
    child.on("exit", () => this.fail(new Error("maintenance filesystem helper exited")));

    const bind = await this.enqueue({ protocol: MAINTENANCE_FS_PROTOCOL, operation: "bind_root", root: this.rootPath });
    validateStatus(bind, "bind_root", new Set(["ok"]));
    const handshake = await this.enqueue({ protocol: MAINTENANCE_FS_PROTOCOL, operation: "handshake" });
    validateHandshake(handshake);
  }

  private request<T>(operation: string, fields: Record<string, unknown>, validate: (value: unknown) => T): Promise<T> {
    if (this.closed) return Promise.reject(new Error("maintenance filesystem helper unavailable"));
    const run = this.sequence.then(async () => {
      await this.readyPromise;
      return validate(await this.enqueue({ protocol: MAINTENANCE_FS_PROTOCOL, operation, ...fields }));
    });
    this.sequence = run.catch(() => undefined);
    return run;
  }

  private enqueue(request: Record<string, unknown>): Promise<unknown> {
    if (this.failed || !this.child || this.pending) return Promise.reject(new Error("maintenance filesystem helper unavailable"));
    const body = Buffer.from(JSON.stringify(request), "utf8");
    if (body.length === 0 || body.length > REQUEST_LIMIT_BYTES) return Promise.reject(new Error("maintenance filesystem helper request too large"));
    const frame = Buffer.allocUnsafe(body.length + 4);
    frame.writeUInt32LE(body.length, 0);
    body.copy(frame, 4);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.fail(new Error("maintenance filesystem helper timeout")), REQUEST_TIMEOUT_MS);
      this.pending = { resolve, reject, timer };
      try {
        this.child!.stdin.write(frame, (error) => {
          body.fill(0); frame.fill(0);
          if (error) this.fail(new Error("maintenance filesystem helper write failed"));
        });
      } catch {
        body.fill(0); frame.fill(0);
        this.fail(new Error("maintenance filesystem helper write failed"));
      }
    });
  }

  private receive(chunk: Buffer): void {
    if (this.failed) return;
    if (this.stdout.length + chunk.length > RESPONSE_LIMIT_BYTES + 4) { this.fail(new Error("maintenance filesystem helper response limit exceeded")); return; }
    this.stdout = Buffer.concat([this.stdout, chunk]);
    if (this.stdout.length < 4) return;
    const length = this.stdout.readUInt32LE(0);
    if (length === 0 || length > RESPONSE_LIMIT_BYTES) { this.fail(new Error("maintenance filesystem helper response limit exceeded")); return; }
    if (this.stdout.length < length + 4) return;
    if (this.stdout.length !== length + 4) { this.fail(new Error("maintenance filesystem helper emitted an invalid frame")); return; }
    const encoded = this.stdout.subarray(4);
    this.stdout = Buffer.alloc(0);
    let parsed: unknown;
    try {
      parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(encoded));
      if (parsed && typeof parsed === "object") Object.defineProperty(parsed, RESPONSE_SIZE, { value: length, enumerable: false });
    }
    catch { this.fail(new Error("maintenance filesystem helper returned malformed JSON")); return; }
    finally { encoded.fill(0); }
    const pending = this.pending;
    if (!pending) { this.fail(new Error("maintenance filesystem helper returned an unsolicited response")); return; }
    this.pending = undefined;
    clearTimeout(pending.timer);
    pending.resolve(parsed);
  }

  private fail(error: Error): void {
    if (this.failed) return;
    this.failed = true;
    const pending = this.pending;
    this.pending = undefined;
    if (pending) { clearTimeout(pending.timer); pending.reject(error); }
    this.stdout.fill(0);
    this.child?.kill();
    this.child = undefined;
  }
}

function validateStatus(value: unknown, operation: string, allowed: Set<string>): void {
  const object = exactObject(value, ["protocol", "operation", "status"]);
  if (object.protocol !== MAINTENANCE_FS_PROTOCOL || object.operation !== operation || typeof object.status !== "string" || !STATUS.has(object.status) || !allowed.has(object.status))
    throw new Error("maintenance filesystem helper response mismatch");
}

function validateHandshake(value: unknown): void {
  const keys = ["protocol", "operation", "status", "maxDepth", "maxEntries", "maxObservedBytes", "maxResponseBytes", "maxDurationMs", "maxHashBytes", "maxTextBytes", "filesystem"];
  const object = exactObject(value, keys);
  if (object.protocol !== MAINTENANCE_FS_PROTOCOL || object.operation !== "handshake" || object.status !== "ok" || object.filesystem !== "NTFS") throw new Error("maintenance filesystem helper protocol mismatch");
  for (const key of keys.slice(3, -1)) if (object[key] !== MAINTENANCE_FS_LIMITS[key as keyof typeof MAINTENANCE_FS_LIMITS]) throw new Error("maintenance filesystem helper limit mismatch");
}

function validateWalk(value: unknown, requested: MaintenanceWalkBudgets): MaintenanceWalkResult {
  const object = exactObject(value, ["protocol", "operation", "status", "complete", "limitation", "returnedEntries", "observedFileBytes", "entries"]);
  const walkStatuses = new Set(["ok", "budget_exhausted", "invalid_request", "not_bound", "unavailable"]);
  if (object.protocol !== MAINTENANCE_FS_PROTOCOL || object.operation !== "walk" || typeof object.status !== "string" || !walkStatuses.has(object.status) ||
      typeof object.complete !== "boolean" || typeof object.limitation !== "string" || !LIMITATION.has(object.limitation) || !Array.isArray(object.entries))
    throw new Error("maintenance filesystem helper walk mismatch");
  if ((object.status === "ok") !== object.complete || (object.status === "budget_exhausted") !== (object.limitation !== "none")) throw new Error("maintenance filesystem helper completeness mismatch");
  validateBoundedInteger(object.returnedEntries, 0, requested.maxEntries);
  validateBoundedInteger(object.observedFileBytes, 0, requested.maxObservedBytes);
  if (responseSize(object) > requested.maxResponseBytes) throw new Error("maintenance filesystem helper response budget mismatch");
  if (object.returnedEntries !== object.entries.length) throw new Error("maintenance filesystem helper entry count mismatch");
  const ids = new Set<string>();
  const paths = new Set<string>();
  let returnedFileBytes = 0;
  const entries = object.entries.map((raw) => {
    const entry = exactObject(raw, ["entryId", "relativePath", "kind", "byteSize", "modifiedUtc", "attributes"]);
    validateEntryId(entry.entryId);
    validateRelativePath(entry.relativePath);
    if (entry.relativePath.split("/").length > requested.maxDepth) throw new Error("maintenance filesystem helper depth mismatch");
    if (ids.has(entry.entryId) || paths.has(entry.relativePath.toLowerCase())) throw new Error("maintenance filesystem helper duplicate entry");
    ids.add(entry.entryId); paths.add(entry.relativePath.toLowerCase());
    if (typeof entry.kind !== "string" || !KIND.has(entry.kind)) throw new Error("maintenance filesystem helper kind mismatch");
    validateBoundedInteger(entry.byteSize, 0, Number.MAX_SAFE_INTEGER);
    if (entry.kind !== "file" && entry.byteSize !== 0) throw new Error("maintenance filesystem helper size mismatch");
    if (entry.kind === "file") returnedFileBytes = checkedSafeAdd(returnedFileBytes, entry.byteSize);
    if (typeof entry.modifiedUtc !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/.test(entry.modifiedUtc) || Number.isNaN(Date.parse(entry.modifiedUtc))) throw new Error("maintenance filesystem helper timestamp mismatch");
    if (typeof entry.attributes !== "string" || !/^(none|readonly(?:,hidden)?(?:,system)?(?:,archive)?|hidden(?:,system)?(?:,archive)?|system(?:,archive)?|archive)$/.test(entry.attributes)) throw new Error("maintenance filesystem helper attributes mismatch");
    return entry as unknown as MaintenanceFsEntry;
  });
  if (returnedFileBytes > object.observedFileBytes || (object.status === "ok" && returnedFileBytes !== object.observedFileBytes)) throw new Error("maintenance filesystem helper observed-byte mismatch");
  if (["invalid_request", "not_bound", "unavailable"].includes(object.status) &&
      (object.complete || object.limitation !== "none" || object.returnedEntries !== 0 || object.observedFileBytes !== 0 || entries.length !== 0))
    throw new Error("maintenance filesystem helper failure-state mismatch");
  return { status: object.status, complete: object.complete, limitation: object.limitation, returnedEntries: object.returnedEntries, observedFileBytes: object.observedFileBytes, entries } as MaintenanceWalkResult;
}

function validateFile(value: unknown, operation: "hash_file" | "read_text_file", requestedEntryId: string, requestedMaximum: number): MaintenanceFileResult {
  const object = exactObject(value, ["protocol", "operation", "status", "entryId", "byteCount", "sha256", "contentBase64"]);
  const fileStatuses = new Set(["ok", "invalid_request", "not_bound", "unavailable", "changed", "invalid_entry", "too_large", ...(operation === "read_text_file" ? ["not_text"] : [])]);
  if (object.protocol !== MAINTENANCE_FS_PROTOCOL || object.operation !== operation || typeof object.status !== "string" || !fileStatuses.has(object.status)) throw new Error("maintenance filesystem helper file mismatch");
  validateEntryId(object.entryId);
  if (object.entryId !== requestedEntryId) throw new Error("maintenance filesystem helper entry ID mismatch");
  validateBoundedInteger(object.byteCount, 0, requestedMaximum);
  if (object.status === "ok") {
    if (typeof object.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(object.sha256)) throw new Error("maintenance filesystem helper digest mismatch");
    if (operation === "hash_file" && object.contentBase64 !== null) throw new Error("maintenance filesystem helper disclosed hash content");
    if (operation === "read_text_file") {
      if (typeof object.contentBase64 !== "string" || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(object.contentBase64)) throw new Error("maintenance filesystem helper content mismatch");
      const bytes = Buffer.from(object.contentBase64, "base64");
      if (bytes.length !== object.byteCount || createHash("sha256").update(bytes).digest("hex") !== object.sha256) throw new Error("maintenance filesystem helper content integrity mismatch");
      try { new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch { throw new Error("maintenance filesystem helper text mismatch"); }
      if (bytes.includes(0)) throw new Error("maintenance filesystem helper binary content mismatch");
    }
  } else if (object.byteCount !== 0 || object.sha256 !== null || object.contentBase64 !== null) throw new Error("maintenance filesystem helper failure disclosed data");
  return object as unknown as MaintenanceFileResult;
}

function validateBudgets(value: MaintenanceWalkBudgets): void {
  validateBoundedInteger(value.maxDepth, 1, MAINTENANCE_FS_LIMITS.maxDepth);
  validateBoundedInteger(value.maxEntries, 1, MAINTENANCE_FS_LIMITS.maxEntries);
  validateBoundedInteger(value.maxObservedBytes, 0, MAINTENANCE_FS_LIMITS.maxObservedBytes);
  validateBoundedInteger(value.maxResponseBytes, 512, MAINTENANCE_FS_LIMITS.maxResponseBytes);
  validateBoundedInteger(value.maxDurationMs, 1, MAINTENANCE_FS_LIMITS.maxDurationMs);
}

function validateEntryId(value: unknown): asserts value is string {
  if (typeof value !== "string" || !/^s[a-f0-9]{8}e[a-f0-9]{8}$/.test(value)) throw new Error("maintenance filesystem helper entry ID mismatch");
}

function validateRelativePath(value: unknown): asserts value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 16_384 || value.includes("\\") || value.startsWith("/") || /^[A-Za-z]:/.test(value)) throw new Error("maintenance filesystem helper path mismatch");
  const segments = value.split("/");
  if (segments.some((segment) => !segment || segment === "." || segment === ".." || segment.length > 255 || segment.includes(":") || segment.includes("\0"))) throw new Error("maintenance filesystem helper path mismatch");
}

function validateBoundedInteger(value: unknown, minimum: number, maximum: number): asserts value is number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum) throw new Error("maintenance filesystem helper integer mismatch");
}

function exactObject(value: unknown, keys: string[]): Record<string, any> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("maintenance filesystem helper schema mismatch");
  const actual = Object.keys(value as object).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) throw new Error("maintenance filesystem helper schema mismatch");
  return value as Record<string, any>;
}

function responseSize(value: Record<string, any>): number {
  const size = (value as Record<PropertyKey, unknown>)[RESPONSE_SIZE];
  if (typeof size !== "number" || !Number.isSafeInteger(size) || size <= 0 || size > RESPONSE_LIMIT_BYTES) throw new Error("maintenance filesystem helper response size mismatch");
  return size;
}

function resolveHelperExecutable(value: string): string {
  if (typeof value !== "string" || !/^[A-Za-z]:[\\/]/.test(value) || value.startsWith("\\\\") || !path.win32.isAbsolute(value))
    throw new Error("maintenance filesystem helper path mismatch");
  const resolved = path.win32.normalize(value);
  if (!/^[A-Za-z]:\\/.test(resolved) || resolved.includes("\0")) throw new Error("maintenance filesystem helper path mismatch");
  return resolved;
}

function checkedSafeAdd(left: number, right: number): number {
  const result = left + right;
  if (!Number.isSafeInteger(result)) throw new Error("maintenance filesystem helper integer overflow");
  return result;
}
