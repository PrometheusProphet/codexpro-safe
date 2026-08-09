import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import { discoverSkillInventory, loadSkill } from "../dist/capabilitiesOps.js";
import { PathGuard } from "../dist/guard.js";
import { readWorkspaceInstructions } from "../dist/workspaceOps.js";

const root = await mkdtemp(path.join(tmpdir(), "codexpro-governance-"));
try {
  const workspaceRoot = path.join(root, "repo");
  await mkdir(path.join(workspaceRoot, "src"), { recursive: true });
  await writeFile(path.join(root, "AGENTS.md"), "parent agents body\n", "utf8");
  await writeFile(path.join(root, "CHATGPT.md"), "parent chatgpt body\n", "utf8");
  await writeFile(path.join(workspaceRoot, "AGENTS.md"), "repository agents body\n", "utf8");
  await writeFile(path.join(workspaceRoot, "src", "AGENTS.md"), "nested agents body\n", "utf8");

  const skillRoot = path.join(workspaceRoot, ".codex", "skills");
  for (let index = 0; index < 125; index += 1) {
    const name = `skill-${String(index).padStart(3, "0")}`;
    await mkdir(path.join(skillRoot, name), { recursive: true });
    const body = index === 124 ? "x".repeat(50_000) : "Use the synthetic skill.";
    await writeFile(path.join(skillRoot, name, "SKILL.md"), `---\nname: ${name}\ndescription: Synthetic fixture.\n---\n${body}\n`, "utf8");
  }
  await mkdir(path.join(skillRoot, "plugin-backup-old", "hidden"), { recursive: true });
  await writeFile(path.join(skillRoot, "plugin-backup-old", "hidden", "SKILL.md"), "---\nname: hidden-backup\ndescription: must not load\n---\n", "utf8");

  const config = {
    defaultRoot: workspaceRoot,
    allowedRoots: [workspaceRoot],
    allowSymlinks: false,
    blockedGlobs: [".git", ".git/**", "**/.git/**"],
    maxReadBytes: 180_000
  };
  const workspace = { id: "fixture", root: workspaceRoot, openedAt: new Date(0).toISOString() };
  const guard = new PathGuard(config);
  const instructions = await readWorkspaceInstructions(config, guard, workspace, { targetPath: "src/file.ts" });
  assert.match(instructions.text, /parent agents body/u);
  assert.match(instructions.text, /parent chatgpt body/u);
  assert.match(instructions.text, /repository agents body/u);
  assert.match(instructions.text, /nested agents body/u);
  assert.doesNotMatch(instructions.text, /\.ai-bridge/u);

  const inventory = await discoverSkillInventory(workspace, { includeGlobal: false, maxSkills: 500 });
  assert.equal(inventory.length, 125);
  assert.equal(inventory.some((skill) => skill.name === "hidden-backup"), false);
  assert.ok(inventory.every((skill) => skill.discovery === "filesystem-candidate" && skill.exposedByCodexPro === true && skill.authoritative === false));
  assert.equal(new Set(inventory.map((skill) => skill.recordId)).size, inventory.length);

  const loaded = await loadSkill(workspace, { name: "skill-124", includeGlobal: false });
  assert.equal(loaded.truncated, false);
  assert.ok(loaded.totalBytes > 40_000);
  console.log("✓ instruction loading and skill discovery contract passed");
} finally {
  await rm(root, { recursive: true, force: true });
}
