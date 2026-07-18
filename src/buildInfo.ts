import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export interface RuntimeBuildInfo {
  packageVersion: string;
  runtimeBuildFingerprint: string;
  runtimeArtifactName: string;
}

function findPackageMetadata(startDir: string): { root: string; version: string } {
  let current = path.resolve(startDir);
  while (true) {
    const packagePath = path.join(current, "package.json");
    if (fs.existsSync(packagePath)) {
      const metadata = JSON.parse(fs.readFileSync(packagePath, "utf8")) as { version?: unknown };
      if (typeof metadata.version !== "string" || !metadata.version) {
        throw new Error(`Package metadata at ${packagePath} does not declare a version.`);
      }
      return { root: current, version: metadata.version };
    }
    const parent = path.dirname(current);
    if (parent === current) throw new Error("Unable to locate CodexPro package metadata.");
    current = parent;
  }
}

export function runtimeBuildInfo(serverModuleUrl: string): RuntimeBuildInfo {
  const serverModulePath = fileURLToPath(serverModuleUrl);
  const metadata = findPackageMetadata(path.dirname(serverModulePath));
  const relativeModulePath = path.relative(metadata.root, serverModulePath).split(path.sep).join("/");
  const executionKind = relativeModulePath.startsWith("dist/") ? "built" : "source";

  return {
    packageVersion: metadata.version,
    runtimeBuildFingerprint: `sha256:${createHash("sha256").update(fs.readFileSync(serverModulePath)).digest("hex")}`,
    runtimeArtifactName: `${executionKind}:${path.basename(serverModulePath)}`
  };
}
