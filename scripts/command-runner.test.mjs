import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { loadConfig } from '../dist/config.js';
import { runCommand } from '../dist/bashOps.js';
import { PathGuard, WorkspaceManager } from '../dist/guard.js';

async function createFixture(bashMode) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-command-runner-'));
  execFileSync('git', ['init', '--quiet', root], { stdio: 'ignore' });
  const config = loadConfig(['--root', root, '--allow-root', root, '--bash', bashMode]);
  const workspace = new WorkspaceManager(config).defaultWorkspace();
  return { root, config, workspace, guard: new PathGuard(config) };
}

test('Windows command runner uses PowerShell and Safe mode preserves its allowlist', async (t) => {
  const fixture = await createFixture('safe');
  t.after(() => fs.rm(fixture.root, { recursive: true, force: true }));
  const status = await runCommand(fixture.config, fixture.guard, fixture.workspace, 'git status --short');
  assert.equal(status.exitCode, 0);
  if (process.platform === 'win32') assert.equal(status.runner, 'powershell');
  await assert.rejects(
    runCommand(fixture.config, fixture.guard, fixture.workspace, 'git remote -v'),
    /allowlist/
  );
});

test('full command mode permits normal repository lifecycle commands against an isolated bare remote', async (t) => {
  const fixture = await createFixture('full');
  const remote = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-command-remote-'));
  t.after(async () => {
    await fs.rm(fixture.root, { recursive: true, force: true });
    await fs.rm(remote, { recursive: true, force: true });
  });
  execFileSync('git', ['init', '--bare', '--quiet', remote], { stdio: 'ignore' });
  const quote = (value) => process.platform === 'win32' ? `'${value.replace(/'/g, "''")}'` : `'${value.replace(/'/g, "'\\''")}'`;
  const command = process.platform === 'win32'
    ? `Set-Content alpha.txt alpha; Move-Item alpha.txt beta.txt; Remove-Item beta.txt; Set-Content committed.txt committed; git add committed.txt; git -c user.name=CodexProTest -c user.email=test@example.invalid commit -m isolated-test; git remote add origin ${quote(remote)}; git push origin HEAD:main`
    : `printf alpha > alpha.txt; mv alpha.txt beta.txt; rm beta.txt; printf committed > committed.txt; git add committed.txt; git -c user.name=CodexProTest -c user.email=test@example.invalid commit -m isolated-test; git remote add origin ${quote(remote)}; git push origin HEAD:main`;
  const result = await runCommand(fixture.config, fixture.guard, fixture.workspace, command, { timeoutMs: 60_000 });
  assert.equal(result.exitCode, 0, `${result.stdout}\n${result.stderr}`);
  assert.equal(await fs.stat(path.join(fixture.root, 'committed.txt')).then(() => true), true);
  assert.equal(await fs.stat(path.join(fixture.root, 'beta.txt')).then(() => true).catch(() => false), false);
  assert.match(execFileSync('git', ['--git-dir', remote, 'show-ref', '--verify', 'refs/heads/main'], { encoding: 'utf8' }), /refs\/heads\/main/);
});
