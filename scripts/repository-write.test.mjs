import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { loadConfig } from '../dist/config.js';
import { writeTextFile, editTextFile } from '../dist/fsOps.js';
import { PathGuard, WorkspaceManager } from '../dist/guard.js';
import { assertWriteToolAllowed } from '../dist/server.js';
import { toolExposureForMode } from '../dist/toolPolicy.js';

async function fixture() {
  const parent = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-repository-write-'));
  const repository = path.join(parent, 'child-repository');
  await fs.mkdir(repository);
  execFileSync('git', ['init', '--quiet', repository], { stdio: 'ignore' });
  return { parent, repository };
}

test('repository write mode accepts the mode, exposes write/edit, and keeps handoff default', async (t) => {
  const { parent } = await fixture();
  t.after(() => fs.rm(parent, { recursive: true, force: true }));
  const repositoryConfig = loadConfig(['--root', parent, '--allow-root', parent, '--write', 'repository']);
  assert.equal(repositoryConfig.writeMode, 'repository');
  assert.equal(loadConfig(['--root', parent]).writeMode, 'handoff');
  const exposure = toolExposureForMode(repositoryConfig);
  assert.equal(exposure.effectiveTools.includes('write'), true);
  assert.equal(exposure.effectiveTools.includes('edit'), true);
});

test('repository write mode rejects a non-Git parent but accepts and confines an opened child repository', async (t) => {
  const { parent, repository } = await fixture();
  t.after(() => fs.rm(parent, { recursive: true, force: true }));
  const config = loadConfig(['--root', parent, '--allow-root', parent, '--write', 'repository']);
  const workspaces = new WorkspaceManager(config);
  const guard = new PathGuard(config);
  const parentWorkspace = workspaces.openWorkspace(parent);
  assert.throws(
    () => assertWriteToolAllowed(config, parentWorkspace, 'blocked.txt'),
    /actual Git worktree root/
  );

  const repositoryWorkspace = workspaces.openWorkspace(repository);
  assert.doesNotThrow(() => assertWriteToolAllowed(config, repositoryWorkspace, 'src/example.txt'));
  assert.throws(() => guard.resolve(repositoryWorkspace, '../outside.txt', { forWrite: true }), /escapes workspace root/);
  await fs.mkdir(path.join(repository, 'src'));
  await writeTextFile(config, guard, repositoryWorkspace, 'src/example.txt', 'before', { createDirs: true });
  await editTextFile(config, guard, repositoryWorkspace, 'src/example.txt', 'before', 'after');
  assert.equal(await fs.readFile(path.join(repository, 'src', 'example.txt'), 'utf8'), 'after');
});

test('handoff and workspace policies retain their previous behavior', async (t) => {
  const { parent } = await fixture();
  t.after(() => fs.rm(parent, { recursive: true, force: true }));
  const handoffConfig = loadConfig(['--root', parent, '--write', 'handoff']);
  const workspace = new WorkspaceManager(handoffConfig).defaultWorkspace();
  assert.doesNotThrow(() => assertWriteToolAllowed(handoffConfig, workspace, '.ai-bridge/current-plan.md'));
  assert.throws(() => assertWriteToolAllowed(handoffConfig, workspace, 'source.ts'), /Source writes are disabled/);
  const workspaceConfig = loadConfig(['--root', parent, '--write', 'workspace']);
  assert.doesNotThrow(() => assertWriteToolAllowed(workspaceConfig, workspace, 'source.ts'));
});
