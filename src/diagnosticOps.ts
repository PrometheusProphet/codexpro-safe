import { Worker } from "node:worker_threads";
import { redactStructured } from "./redact.js";
import type {
  NativeDatabaseResponse,
  NativeDiagnosticEntry,
  NativeDiagnosticFile,
  WindowsDiagnosticBoundary
} from "./windowsDiagnosticBoundary.js";

const MAX_RESPONSE_BYTES = 64 * 1024;
const MAX_RECORDS = 100;
const MAX_NATIVE_ENTRIES = 512;
const MAX_DATABASE_BYTES = 32 * 1024 * 1024;
const OPERATION_TIMEOUT_MS = 3_000;
const SQLITE_WORKER_TIMEOUT_MS = 2_000;
const CONFIG_FILES = ["config.toml", "config.toml.bak", "config.toml.backup"] as const;
const RUNTIME_CATEGORIES = ["skills", "plugins", "logs", "sessions"] as const;
const FAMILY_PATTERNS = {
  logs: /^logs_[A-Za-z0-9_-]+\.sqlite$/,
  state: /^state_[A-Za-z0-9_-]+\.sqlite$/,
  memories: /^memories_[A-Za-z0-9_-]+\.sqlite$/,
  goals: /^goals_[A-Za-z0-9_-]+\.sqlite$/
} as const;

export type DiagnosticFamilyKind = keyof typeof FAMILY_PATTERNS;
export type DiagnosticSqliteOperation = "summary" | "integrity_check" | "storage_metadata" | "schema" | "row_counts" | "timestamp_ranges";

export interface DiagnosticOperationsOptions {
  /** Internal-only boundary seam. Never exposed by connector configuration or MCP inputs. */
  boundaryForTest?: WindowsDiagnosticBoundary;
  /** Manager-trusted production boundary. */
  boundary?: WindowsDiagnosticBoundary;
  /** Internal-only worker termination seam. */
  sqliteWorkerDelayMsForTest?: number;
}

function result(status: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return boundResponse({ status, ...extra });
}

function boundResponse(value: Record<string, unknown>): Record<string, unknown> {
  const sanitized = redactStructured(value);
  const encoded = JSON.stringify(sanitized);
  if (Buffer.byteLength(encoded, "utf8") <= MAX_RESPONSE_BYTES) return sanitized;
  return { status: "truncated", truncated: true, record_limit: MAX_RECORDS, response_limit_bytes: MAX_RESPONSE_BYTES };
}

function safeFailure(extra: Record<string, unknown> = {}): Record<string, unknown> {
  return result("unavailable", { reason: "diagnostic operation could not safely complete", ...extra });
}

function withTimeout<T>(operation: Promise<T>): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  const timeout = new Promise<T>((_, reject) => {
    timer = setTimeout(() => reject(new Error("diagnostic timeout")), OPERATION_TIMEOUT_MS);
  });
  return Promise.race([operation, timeout]).finally(() => timer && clearTimeout(timer));
}

function validName(name: unknown): name is string {
  return typeof name === "string" && name.length > 0 && name.length <= 255 &&
    name !== "." && name !== ".." && !/[\\/:\0]/.test(name);
}

function validTimestamp(value: unknown): value is string {
  if (typeof value !== "string" || value.length > 40) return false;
  const parsed = new Date(value);
  return !Number.isNaN(parsed.valueOf()) && parsed.toISOString() === value;
}

function validEntry(value: unknown): value is NativeDiagnosticEntry {
  if (!value || typeof value !== "object") return false;
  const entry = value as NativeDiagnosticEntry;
  return validName(entry.name) && typeof entry.isDirectory === "boolean" && typeof entry.isReparsePoint === "boolean" &&
    Number.isSafeInteger(entry.bytes) && entry.bytes >= 0 && validTimestamp(entry.modifiedUtc);
}

function validatedEntries(value: unknown): NativeDiagnosticEntry[] | undefined {
  if (!Array.isArray(value) || value.length > MAX_NATIVE_ENTRIES || !value.every(validEntry)) return undefined;
  const names = new Set<string>();
  for (const entry of value) {
    const key = entry.name.toLowerCase();
    if (names.has(key)) return undefined;
    names.add(key);
  }
  return value;
}

function direct(entries: NativeDiagnosticEntry[], name: string): NativeDiagnosticEntry | undefined {
  return entries.find((entry) => entry.name.toLowerCase() === name.toLowerCase());
}

function databaseEntries(entries: NativeDiagnosticEntry[]): NativeDiagnosticEntry[] {
  return entries.filter((entry) => !entry.isDirectory && !entry.isReparsePoint &&
    Object.values(FAMILY_PATTERNS).some((pattern) => pattern.test(entry.name)));
}

function categorySummary(entries: NativeDiagnosticEntry[]): Record<string, unknown> {
  const categories: Record<string, unknown> = {};
  for (const category of RUNTIME_CATEGORIES) {
    const directory = direct(entries, category);
    if (!directory) categories[category] = { status: "absent", count: 0, bytes: 0 };
    else if (!directory.isDirectory || directory.isReparsePoint) categories[category] = { status: "rejected", count: 0, bytes: 0 };
    else categories[category] = { status: "present", count: 1, bytes: directory.bytes };
  }
  const databases = databaseEntries(entries);
  categories.databases = { status: "present", count: databases.length, bytes: databases.reduce((sum, entry) => sum + entry.bytes, 0) };
  categories.configuration = {
    status: "present",
    count: CONFIG_FILES.filter((name) => {
      const entry = direct(entries, name);
      return entry && !entry.isDirectory && !entry.isReparsePoint;
    }).length,
    bytes: 0
  };
  return categories;
}

function databaseMetadata(entries: NativeDiagnosticEntry[]): { items: Record<string, unknown>[]; truncated: boolean } {
  const matches = databaseEntries(entries);
  const items = matches.slice(0, MAX_RECORDS).map((entry) => {
    const family = Object.entries(FAMILY_PATTERNS).find(([, pattern]) => pattern.test(entry.name))?.[0] ?? "unknown";
    const sidecars = ["wal", "shm"].filter((kind) => {
      const sidecar = direct(entries, `${entry.name}-${kind}`);
      return sidecar && !sidecar.isDirectory && !sidecar.isReparsePoint;
    });
    return { family_kind: family, filename: entry.name, coarse_type: "database", bytes: entry.bytes, modified_utc: entry.modifiedUtc, sidecars };
  });
  return { items, truncated: matches.length > MAX_RECORDS };
}

function decodeFile(file: NativeDiagnosticFile | undefined, maximumBytes: number): Buffer | undefined {
  if (!file || !validName(file.name) || !Number.isSafeInteger(file.bytes) || file.bytes < 0 || file.bytes > maximumBytes ||
      !validTimestamp(file.modifiedUtc) || typeof file.contentBase64 !== "string") return undefined;
  if (file.contentBase64.length > Math.ceil(maximumBytes / 3) * 4 + 4 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(file.contentBase64)) return undefined;
  const bytes = Buffer.from(file.contentBase64, "base64");
  if (bytes.length !== file.bytes || bytes.toString("base64") !== file.contentBase64) return undefined;
  return bytes;
}

const ALLOWED_CONFIG: Record<string, Record<string, { type: "boolean" | "integer" | "enum"; values?: readonly string[] }>> = {
  general: { telemetry: { type: "boolean" }, max_threads: { type: "integer" }, sandbox_mode: { type: "enum", values: ["off", "workspace-write", "read-only"] } },
  features: { web_search: { type: "boolean" }, image_generation: { type: "boolean" } }
};

function configValue(raw: string, allowed: { type: "boolean" | "integer" | "enum"; values?: readonly string[] }): unknown {
  const value = raw.trim();
  if (allowed.type === "boolean") return value === "true" || value === "false" ? value === "true" : "invalid";
  if (allowed.type === "integer") {
    const parsed = Number(value);
    return Number.isInteger(parsed) && parsed >= 0 && parsed <= 1_000_000 ? parsed : "invalid";
  }
  const unquoted = value.replace(/^"|"$/g, "");
  return allowed.values?.includes(unquoted) ? unquoted : "invalid";
}

function parseConfiguration(text: string): Record<string, unknown> {
  let section = "";
  let recognized = 0;
  let omitted = 0;
  const approved: Record<string, unknown> = {};
  for (const line of text.split(/\r?\n/).slice(0, 4_000)) {
    const sectionMatch = line.match(/^\s*\[([A-Za-z0-9_-]+)\]\s*(?:#.*)?$/);
    if (sectionMatch) { section = sectionMatch[1]; continue; }
    const match = line.match(/^\s*([A-Za-z0-9_-]+)\s*=\s*(.*?)\s*(?:#.*)?$/);
    if (!match) { if (line.trim() && !line.trim().startsWith("#")) omitted += 1; continue; }
    const key = match[1];
    const allow = ALLOWED_CONFIG[section]?.[key];
    if (!allow) { omitted += 1; continue; }
    approved[`${section}.${key}`] = configValue(match[2], allow);
    recognized += 1;
  }
  return { approved, recognized_keys: recognized, omitted_items: omitted };
}

function analyzeSqliteInWorker(bytes: Buffer, operation: DiagnosticSqliteOperation, delayMsForTest: number): Promise<Record<string, unknown>> {
  const transferred = Uint8Array.from(bytes);
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL("./diagnosticSqliteWorker.js", import.meta.url), {
      workerData: { bytes: transferred.buffer, operation, delayMsForTest },
      transferList: [transferred.buffer],
      resourceLimits: { maxOldGenerationSizeMb: 128, maxYoungGenerationSizeMb: 16, stackSizeMb: 4 }
    });
    let settled = false;
    const finish = (error?: Error, value?: Record<string, unknown>) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      void worker.terminate();
      if (error) reject(error);
      else resolve(value ?? {});
    };
    const timer = setTimeout(() => finish(new Error("SQLite analysis timeout")), SQLITE_WORKER_TIMEOUT_MS);
    worker.once("message", (value: unknown) => {
      if (!value || typeof value !== "object" || Array.isArray(value)) finish(new Error("invalid SQLite worker response"));
      else finish(undefined, value as Record<string, unknown>);
    });
    worker.once("error", () => finish(new Error("SQLite worker failed")));
    worker.once("exit", (code) => {
      if (!settled && code !== 0) finish(new Error("SQLite worker exited"));
    });
  });
}

function validDatabaseEnvelope(value: NativeDatabaseResponse, familyKind: DiagnosticFamilyKind): boolean {
  if (!value || !["ok", "missing", "ambiguous", "oversized", "unavailable"].includes(value.status)) return false;
  if (value.status === "ambiguous") return Number.isInteger(value.matches) && Number(value.matches) > 1 && Number(value.matches) <= MAX_NATIVE_ENTRIES;
  if (value.status === "ok" || value.status === "oversized") {
    const file = value.database;
    return Boolean(file && FAMILY_PATTERNS[familyKind].test(file.name) && validTimestamp(file.modifiedUtc) && Number.isSafeInteger(file.bytes) && file.bytes >= 0 &&
      Array.isArray(value.sidecars) && value.sidecars.every((sidecar) => sidecar === "wal" || sidecar === "shm"));
  }
  return true;
}

export class CodexDiagnosticOperations {
  private readonly boundary?: WindowsDiagnosticBoundary;
  private readonly sqliteWorkerDelayMsForTest: number;

  constructor(options: DiagnosticOperationsOptions = {}) {
    this.boundary = options.boundaryForTest ?? options.boundary;
    this.sqliteWorkerDelayMsForTest = Math.max(0, Math.min(options.sqliteWorkerDelayMsForTest ?? 0, 10_000));
  }

  async inventory(): Promise<Record<string, unknown>> {
    return withTimeout(this.inventoryInner()).catch(() => result("unavailable", { installed_extensions: { status: "unavailable" } }));
  }

  private async inventoryInner(): Promise<Record<string, unknown>> {
    if (!this.boundary) return result("unavailable", { installed_extensions: { status: "unavailable" } });
    const native = await this.boundary.inventory();
    if (native.status === "missing") return result("missing", { categories: categorySummary([]), installed_extensions: { status: "unavailable" }, process_locks: { status: "unavailable" } });
    if (native.status !== "ok") return result("unavailable", { installed_extensions: { status: "unavailable" } });
    const entries = validatedEntries(native.entries);
    if (!entries) return result("unavailable", { installed_extensions: { status: "unavailable" } });
    const databases = databaseMetadata(entries);
    return result("ok", {
      categories: categorySummary(entries),
      databases: databases.items,
      installed_extensions: { status: "unavailable" },
      truncated: databases.truncated,
      process_locks: { status: "unavailable" }
    });
  }

  async configurationSummary(): Promise<Record<string, unknown>> {
    return withTimeout(this.configurationSummaryInner()).catch(() => safeFailure());
  }

  private async configurationSummaryInner(): Promise<Record<string, unknown>> {
    if (!this.boundary) return safeFailure();
    const native = await this.boundary.configuration();
    if (native.status === "missing") return result("missing", { files: [] });
    if (native.status !== "ok" || !Array.isArray(native.files) || native.files.length !== CONFIG_FILES.length) return safeFailure();
    const files: Record<string, unknown>[] = [];
    for (let index = 0; index < CONFIG_FILES.length; index += 1) {
      const name = CONFIG_FILES[index];
      const file = native.files[index];
      if (!file || file.name !== name || !["present", "absent"].includes(file.status)) return safeFailure();
      if (file.status === "absent") { files.push({ config_file: name, status: "absent" }); continue; }
      const content = decodeFile(file, MAX_RESPONSE_BYTES);
      if (!content) return safeFailure();
      files.push({ config_file: name, status: "present", ...parseConfiguration(content.toString("utf8")) });
    }
    return result("ok", { files });
  }

  async sqliteMetadata(familyKind: DiagnosticFamilyKind, operation: DiagnosticSqliteOperation): Promise<Record<string, unknown>> {
    return withTimeout(this.sqliteMetadataInner(familyKind, operation)).catch(() => safeFailure({ family_kind: familyKind }));
  }

  private async sqliteMetadataInner(familyKind: DiagnosticFamilyKind, operation: DiagnosticSqliteOperation): Promise<Record<string, unknown>> {
    if (!this.boundary) return safeFailure({ family_kind: familyKind });
    const native = await this.boundary.database(familyKind);
    if (!validDatabaseEnvelope(native, familyKind)) return safeFailure({ family_kind: familyKind });
    if (native.status === "missing") return result("missing", { family_kind: familyKind });
    if (native.status === "ambiguous") return result("ambiguous", { family_kind: familyKind, matches: native.matches });
    if (native.status === "oversized") return result("unavailable", { family_kind: familyKind, reason: "database exceeds diagnostic size limit" });
    if (native.status !== "ok") return safeFailure({ family_kind: familyKind });
    const bytes = decodeFile(native.database, MAX_DATABASE_BYTES);
    if (!bytes) return safeFailure({ family_kind: familyKind });

    try {
      const selected = native.database!;
      const base = { status: "ok", family_kind: familyKind, database: { filename: selected.name, bytes: selected.bytes, sidecars: native.sidecars } };
      const analysis = await analyzeSqliteInWorker(bytes, operation, this.sqliteWorkerDelayMsForTest);
      return result("ok", { ...base, ...analysis });
    } catch {
      return result("unavailable", { family_kind: familyKind, reason: "database metadata could not be safely read" });
    } finally {
      bytes.fill(0);
    }
  }
}
