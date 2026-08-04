import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs/promises";

const REQUEST_LIMIT_BYTES = 4 * 1024;
const RESPONSE_LIMIT_BYTES = 48 * 1024 * 1024;
const STDERR_LIMIT_BYTES = 8 * 1024;
const REQUEST_TIMEOUT_MS = 2_500;

export const DIAGNOSTIC_HELPER_PROTOCOL = "codexpro-diagnostic-v1";

export interface NativeDiagnosticEntry {
  name: string;
  isDirectory: boolean;
  isReparsePoint: boolean;
  bytes: number;
  modifiedUtc: string;
}

export interface NativeDiagnosticFile {
  name: string;
  bytes: number;
  modifiedUtc: string;
  contentBase64?: string;
}

export interface NativeInventoryResponse {
  status: "ok" | "missing" | "unavailable";
  entries?: NativeDiagnosticEntry[];
}

export interface NativeConfigurationResponse {
  status: "ok" | "missing" | "unavailable";
  files?: Array<NativeDiagnosticFile & { status: "present" | "absent" }>;
}

export interface NativeDatabaseResponse {
  status: "ok" | "missing" | "ambiguous" | "oversized" | "unavailable";
  matches?: number;
  database?: NativeDiagnosticFile;
  sidecars?: string[];
}

export interface WindowsDiagnosticBoundary {
  inventory(): Promise<NativeInventoryResponse>;
  configuration(): Promise<NativeConfigurationResponse>;
  database(familyKind: string): Promise<NativeDatabaseResponse>;
  close(): void;
}

export interface WindowsDiagnosticBoundaryOptions {
  executablePath: string;
  expectedProtocol: string;
  expectedSha256: string;
  /** Internal-only process seam for deterministic framing/failure tests. */
  commandForTest?: string;
  /** Internal-only process seam for deterministic framing/failure tests. */
  argumentsForTest?: string[];
  /** Internal-only timeout seam. */
  timeoutMsForTest?: number;
}

interface PendingResponse {
  resolve: (value: any) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
}

export class ManagedWindowsDiagnosticBoundary implements WindowsDiagnosticBoundary {
  private readonly options: WindowsDiagnosticBoundaryOptions;
  private readonly readyPromise: Promise<void>;
  private child?: ChildProcessWithoutNullStreams;
  private pending?: PendingResponse;
  private stdout = Buffer.alloc(0);
  private stderrBytes = 0;
  private failed = false;
  private sequence: Promise<unknown> = Promise.resolve();

  constructor(options: WindowsDiagnosticBoundaryOptions) {
    this.options = options;
    this.readyPromise = this.start();
    // Keep startup failures fail-closed until a diagnostic request observes
    // them; never let a missing or malformed helper become an unhandled
    // rejection that terminates the connector.
    void this.readyPromise.catch(() => undefined);
  }

  inventory(): Promise<NativeInventoryResponse> {
    return this.request({ operation: "inventory" });
  }

  configuration(): Promise<NativeConfigurationResponse> {
    return this.request({ operation: "configuration" });
  }

  database(familyKind: string): Promise<NativeDatabaseResponse> {
    return this.request({ operation: "database", familyKind });
  }

  close(): void {
    this.fail(new Error("diagnostic helper closed"));
  }

  /** Completes after fingerprint verification and protocol handshake. */
  async ready(): Promise<void> {
    await this.readyPromise;
  }

  private async start(): Promise<void> {
    if (process.platform !== "win32" && !this.options.commandForTest) throw new Error("Windows diagnostic helper unavailable");
    const executable = await fs.readFile(this.options.executablePath);
    const actualHash = createHash("sha256").update(executable).digest("hex");
    if (actualHash !== this.options.expectedSha256.toLowerCase()) throw new Error("diagnostic helper fingerprint mismatch");

    const command = this.options.commandForTest ?? this.options.executablePath;
    const args = this.options.argumentsForTest ?? ["--serve"];
    const allowedEnvironment = ["SystemRoot", "WINDIR", "USERPROFILE", "TEMP", "TMP"];
    const environment = Object.fromEntries(allowedEnvironment.flatMap((name) => process.env[name] ? [[name, process.env[name]!]] : []));
    const child = spawn(command, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true, shell: false, env: environment });
    this.child = child;
    child.stdout.on("data", (chunk: Buffer) => this.receive(chunk));
    child.stderr.on("data", (chunk: Buffer) => {
      this.stderrBytes += chunk.length;
      if (this.stderrBytes > STDERR_LIMIT_BYTES) this.fail(new Error("diagnostic helper stderr limit exceeded"));
    });
    child.on("error", () => this.fail(new Error("diagnostic helper failed to start")));
    child.on("exit", () => this.fail(new Error("diagnostic helper exited")));

    const handshake = await this.enqueue({ operation: "handshake" });
    this.validateEnvelope(handshake);
  }

  private request<T>(request: Record<string, unknown>): Promise<T> {
    const run = this.sequence.then(async () => {
      await this.readyPromise;
      const response = await this.enqueue(request);
      this.validateEnvelope(response);
      return response as T;
    });
    this.sequence = run.catch(() => undefined);
    return run;
  }

  private enqueue(request: Record<string, unknown>): Promise<any> {
    if (this.failed || !this.child || this.pending) return Promise.reject(new Error("diagnostic helper unavailable"));
    const body = Buffer.from(JSON.stringify({ protocol: this.options.expectedProtocol, ...request }), "utf8");
    if (body.length > REQUEST_LIMIT_BYTES) return Promise.reject(new Error("diagnostic helper request too large"));
    const frame = Buffer.allocUnsafe(body.length + 4);
    frame.writeUInt32LE(body.length, 0);
    body.copy(frame, 4);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => this.fail(new Error("diagnostic helper timeout")), this.options.timeoutMsForTest ?? REQUEST_TIMEOUT_MS);
      this.pending = { resolve, reject, timer };
      this.child!.stdin.write(frame, (error) => error && this.fail(new Error("diagnostic helper write failed")));
    });
  }

  private receive(chunk: Buffer): void {
    if (this.failed) return;
    this.stdout = Buffer.concat([this.stdout, chunk]);
    if (this.stdout.length < 4) return;
    const length = this.stdout.readUInt32LE(0);
    if (length === 0 || length > RESPONSE_LIMIT_BYTES) {
      this.fail(new Error("diagnostic helper response limit exceeded"));
      return;
    }
    if (this.stdout.length < length + 4) return;
    if (this.stdout.length !== length + 4) {
      this.fail(new Error("diagnostic helper emitted an invalid frame"));
      return;
    }
    const encoded = this.stdout.subarray(4);
    this.stdout = Buffer.alloc(0);
    let parsed: unknown;
    try {
      parsed = JSON.parse(encoded.toString("utf8"));
    } catch {
      this.fail(new Error("diagnostic helper returned malformed JSON"));
      return;
    }
    const pending = this.pending;
    if (!pending) {
      this.fail(new Error("diagnostic helper returned an unsolicited response"));
      return;
    }
    this.pending = undefined;
    clearTimeout(pending.timer);
    pending.resolve(parsed);
  }

  private validateEnvelope(value: any): void {
    if (!value || typeof value !== "object" || value.protocol !== this.options.expectedProtocol || value.helperVersion !== this.options.expectedProtocol) {
      this.fail(new Error("diagnostic helper protocol mismatch"));
      throw new Error("diagnostic helper protocol mismatch");
    }
  }

  private fail(error: Error): void {
    if (this.failed) return;
    this.failed = true;
    const pending = this.pending;
    this.pending = undefined;
    if (pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.child?.kill();
    this.child = undefined;
  }
}
