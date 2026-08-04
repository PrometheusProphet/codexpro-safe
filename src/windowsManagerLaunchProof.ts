import { spawn } from "node:child_process";
import os from "node:os";
import path from "node:path";
import type { CodexDiagnosticHelperConfig } from "./config.js";

export const MANAGER_LAUNCH_PROTOCOL = "codexpro-manager-launch-v1";
const PIPE_PATTERN = /^codexpro-safe-diagnostic-[a-f0-9]{32}$/;
const MAX_FRAME_BYTES = 4096;
const MAX_STDERR_BYTES = 4096;

export interface ManagerLaunchProof {
  protocol: string;
  status: "ok";
  instanceId: string;
  managerPid: number;
  launcherPid: number;
  serverPid: number;
  verifierPid: number;
  issuedUtc: string;
  expiresUtc: string;
  helper: CodexDiagnosticHelperConfig;
}

export interface ManagerLaunchProofOptions {
  pipeName: string;
  managerExecutableForTest?: string;
  commandForTest?: string;
  argumentsForTest?: string[];
  timeoutMsForTest?: number;
  capabilityForTest?: string;
}

function installedManagerExecutable(): string {
  return path.join(
    os.userInfo().homedir,
    "AppData",
    "Local",
    "Programs",
    "CodexProSafe Manager",
    "CodexProSafe.Manager.exe"
  );
}

function parseProof(frame: Buffer, pipeName: string): ManagerLaunchProof | undefined {
  if (frame.length < 5) return undefined;
  const length = frame.readUInt32LE(0);
  if (length <= 0 || length > MAX_FRAME_BYTES || frame.length !== length + 4) return undefined;
  let value: unknown;
  try {
    value = JSON.parse(frame.subarray(4).toString("utf8"));
  } catch {
    return undefined;
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const proof = value as Partial<ManagerLaunchProof>;
  const helper = proof.helper as Partial<CodexDiagnosticHelperConfig> | undefined;
  const issued = Date.parse(String(proof.issuedUtc ?? ""));
  const expires = Date.parse(String(proof.expiresUtc ?? ""));
  const now = Date.now();
  if (
    proof.protocol !== MANAGER_LAUNCH_PROTOCOL ||
    proof.status !== "ok" ||
    proof.instanceId !== pipeName ||
    !Number.isInteger(proof.managerPid) || Number(proof.managerPid) <= 0 ||
    !Number.isInteger(proof.launcherPid) || Number(proof.launcherPid) <= 0 ||
    !Number.isInteger(proof.serverPid) || Number(proof.serverPid) <= 0 ||
    !Number.isInteger(proof.verifierPid) || Number(proof.verifierPid) <= 0 ||
    !Number.isFinite(issued) || !Number.isFinite(expires) ||
    issued > now + 2_000 || now > expires || expires - issued > 20_000 ||
    !helper || typeof helper.executablePath !== "string" ||
    !path.isAbsolute(helper.executablePath) ||
    path.basename(helper.executablePath).toLowerCase() !== "codexprosafe.diagnostichelper.exe" ||
    helper.protocolVersion !== "codexpro-diagnostic-v1" ||
    typeof helper.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(helper.sha256)
  ) {
    return undefined;
  }
  return {
    ...(proof as ManagerLaunchProof),
    helper: { ...helper, executablePath: path.resolve(helper.executablePath) } as CodexDiagnosticHelperConfig
  };
}

export async function resolveWindowsManagerLaunchProof(
  options: ManagerLaunchProofOptions
): Promise<ManagerLaunchProof | undefined> {
  if (!PIPE_PATTERN.test(options.pipeName)) return undefined;
  if (process.platform !== "win32" && !options.commandForTest) return undefined;

  const executable = options.commandForTest
    ?? options.managerExecutableForTest
    ?? installedManagerExecutable();
  const args = options.argumentsForTest
    ?? ["--diagnostic-launch-proof-client", options.pipeName];
  const timeoutMs = options.timeoutMsForTest ?? 3_500;
  const capability = options.capabilityForTest ?? await readLaunchCapability();
  if (!/^[a-f0-9]{64}$/.test(capability ?? "")) return undefined;

  return await new Promise((resolve) => {
    let stdout = Buffer.alloc(0);
    let stderrBytes = 0;
    let settled = false;
    let timer: NodeJS.Timeout | undefined;
    const finish = (value: ManagerLaunchProof | undefined) => {
      if (settled) return;
      settled = true;
      if (timer) clearTimeout(timer);
      resolve(value);
    };
    let child;
    try {
      child = spawn(executable, args, {
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"],
        env: {}
      });
    } catch {
      finish(undefined);
      return;
    }
    timer = setTimeout(() => {
      child.kill();
      finish(undefined);
    }, timeoutMs);
    child.stdin.end(capability);
    child.stdout.on("data", (chunk: Buffer) => {
      if (stdout.length + chunk.length > MAX_FRAME_BYTES + 4) {
        child.kill();
        finish(undefined);
        return;
      }
      stdout = Buffer.concat([stdout, chunk]);
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderrBytes += chunk.length;
      if (stderrBytes > MAX_STDERR_BYTES) {
        child.kill();
        finish(undefined);
      }
    });
    child.once("error", () => finish(undefined));
    child.once("close", (code) => finish(code === 0 ? parseProof(stdout, options.pipeName) : undefined));
  });
}

async function readLaunchCapability(): Promise<string | undefined> {
  return await new Promise((resolve) => {
    let received = Buffer.alloc(0);
    let settled = false;
    const finish = (value: string | undefined) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      process.stdin.off("data", onData);
      process.stdin.off("end", onEnd);
      process.stdin.pause();
      resolve(value);
    };
    const onData = (chunk: Buffer) => {
      received = Buffer.concat([received, chunk]);
      if (received.length > 64) finish(undefined);
      else if (received.length === 64) {
        const value = received.toString("ascii");
        finish(/^[a-f0-9]{64}$/.test(value) ? value : undefined);
      }
    };
    const onEnd = () => finish(undefined);
    const timer = setTimeout(() => finish(undefined), 1_500);
    process.stdin.on("data", onData);
    process.stdin.once("end", onEnd);
    process.stdin.resume();
  });
}
