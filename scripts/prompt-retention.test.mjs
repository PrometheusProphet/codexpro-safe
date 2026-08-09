import assert from "node:assert/strict";
import { mkdtemp, mkdir, readdir, rm, utimes, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import { PathGuard } from "../dist/guard.js";
import { savePromptFile } from "../dist/promptFileOps.js";

const root = await mkdtemp(path.join(tmpdir(), "codexpro-prompt-retention-"));
try {
  const promptRoot = path.join(root, ".ai-bridge", "prompts"); await mkdir(promptRoot, { recursive: true });
  for (let index = 0; index < 25; index += 1) {
    const file = path.join(promptRoot, `prompt-${String(index).padStart(2, "0")}.md`); await writeFile(file, `prompt ${index}\n`, "utf8");
    const time = new Date(Date.UTC(2026, 0, 1, 0, 0, index)); await utimes(file, time, time);
  }
  const config = { defaultRoot: root, allowedRoots: [root], allowSymlinks: false, blockedGlobs: [".git", ".git/**", "**/.git/**"], contextDir: ".ai-bridge", maxReadBytes: 100_000, maxWriteBytes: 100_000 };
  const workspace = { id: "fixture", root, openedAt: new Date(0).toISOString() };
  const result = await savePromptFile(config, new PathGuard(config), workspace, { filename: "fresh.md", prompt: "Execute the synthetic bounded task." });
  const remaining = (await readdir(promptRoot)).filter((name) => name.endsWith(".md"));
  assert.equal(result.retiredPromptFiles, 6); assert.equal(remaining.length, 20); assert.ok(remaining.includes("fresh.md")); assert.equal(remaining.includes("prompt-00.md"), false);
  console.log("✓ active prompt retention contract passed");
} finally { await rm(root, { recursive: true, force: true }); }
