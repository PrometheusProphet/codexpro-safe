import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';

const npmInvocation = process.platform === 'win32'
  ? [process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', 'npm pack --dry-run --json --silent']]
  : ['npm', ['pack', '--dry-run', '--json', '--silent']];
const packed = spawnSync(npmInvocation[0], npmInvocation[1], {
  cwd: process.cwd(),
  encoding: 'utf8',
  maxBuffer: 10 * 1024 * 1024,
});

assert.equal(packed.status, 0, packed.error?.message || packed.stderr || packed.stdout || 'npm pack failed');
const result = JSON.parse(packed.stdout)[0];
const paths = result.files.map((file) => file.path);
const runtimeScripts = [
  'scripts/build-freshness.mjs',
  'scripts/cli-ui.mjs',
  'scripts/codexpro.mjs',
  'scripts/handoff-execution.mjs',
  'scripts/profile-settings.mjs',
  'scripts/pro-apply.mjs',
  'scripts/pro-bundle.mjs',
  'scripts/refresh-mcp-runtime-status.ps1',
];

for (const required of runtimeScripts) {
  assert.ok(paths.includes(required), `missing packaged runtime script: ${required}`);
}

const unexpectedScripts = paths.filter(
  (file) => file.startsWith('scripts/') && !runtimeScripts.includes(file),
);
assert.deepEqual(unexpectedScripts, [], `unexpected packaged scripts: ${unexpectedScripts.join(', ')}`);
assert.equal(paths.some((file) => file.includes('/fixtures/')), false, 'package contains test fixtures');

console.log(`package contents contract passed (${result.entryCount} files, ${result.unpackedSize} unpacked bytes)`);
