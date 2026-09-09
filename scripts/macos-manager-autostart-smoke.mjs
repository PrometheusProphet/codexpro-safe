import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

if (process.platform !== 'darwin') throw new Error('macOS Manager auto-start smoke test requires macOS.');

const root = fs.realpathSync(process.cwd());
const manager = path.join(root, 'artifacts/macos/CodexPro-Safe Manager.app/Contents/MacOS/CodexProSafeManager');
assert.ok(fs.statSync(manager).isFile(), 'Package the macOS Manager before running its auto-start smoke test.');

const port = await new Promise((resolve, reject) => {
  const server = net.createServer();
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => {
    const address = server.address();
    assert.ok(address && typeof address === 'object');
    server.close(() => resolve(address.port));
  });
});
const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'codexpro-safe-manager-autostart-'));
const settingsPath = path.join(temporaryRoot, 'settings.json');
fs.writeFileSync(settingsPath, JSON.stringify({
  repository: root,
  workspaceRoot: root,
  allowedRoot: path.dirname(root),
  nodePath: process.execPath,
  port,
  accessProfile: 'planning',
  tunnelMode: 'none',
  hostname: '',
  restartOnFailure: true,
  autoStartServices: true
}));

const app = spawn(manager, [], {
  cwd: root,
  env: { ...process.env, CODEXPRO_MANAGER_SETTINGS: settingsPath, NO_COLOR: '1', CI: '1' },
  stdio: ['ignore', 'pipe', 'pipe']
});
let output = '';
app.stdout.on('data', (chunk) => { output += String(chunk); });
app.stderr.on('data', (chunk) => { output += String(chunk); });

function listenerGroup() {
  const listeners = spawnSync('/usr/sbin/lsof', ['-nP', `-iTCP:${port}`, '-sTCP:LISTEN', '-t'], { encoding: 'utf8' });
  const pid = Number(listeners.stdout.trim().split(/\s+/)[0]);
  if (!Number.isInteger(pid) || pid <= 0) return undefined;
  const group = Number(spawnSync('/bin/ps', ['-o', 'pgid=', '-p', String(pid)], { encoding: 'utf8' }).stdout.trim());
  return Number.isInteger(group) && group > 0 ? group : undefined;
}

async function waitForGroup(excludedGroup) {
  const deadline = Date.now() + 20_000;
  while (Date.now() < deadline) {
    if (app.exitCode !== null) throw new Error(`Manager exited early (${app.exitCode}).\n${output.slice(-2_000)}`);
    const group = listenerGroup();
    if (group && group !== excludedGroup) {
      try { if (await healthIsReady()) return group; } catch {}
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Manager-owned connector did not become healthy.\n${output.slice(-2_000)}`);
}

function healthIsReady() {
  return new Promise((resolve, reject) => {
    const request = http.get({ host: '127.0.0.1', port, path: '/healthz', agent: false },
      (response) => {
        response.resume();
        resolve(response.statusCode === 200);
      });
    request.setTimeout(1_000, () => request.destroy(new Error('health timeout')));
    request.once('error', reject);
  });
}

async function verifyToolCall() {
  const client = new Client({ name: 'macos-manager-autostart-smoke', version: '0.0.0' });
  try {
    await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
    const result = await client.callTool({ name: 'server_config', arguments: {} });
    assert.notEqual(result.isError, true, result.content?.[0]?.text);
    assert.equal(result.structuredContent.writeMode, 'handoff');
    assert.equal(result.structuredContent.bashMode, 'off');
  } finally {
    await client.close().catch(() => {});
  }
}

try {
  const firstGroup = await waitForGroup();
  await verifyToolCall();
  process.kill(-firstGroup, 'SIGKILL');
  const recoveredGroup = await waitForGroup(firstGroup);
  assert.notEqual(recoveredGroup, firstGroup);
  await verifyToolCall();
  console.log('✓ installed-style auto-start and unexpected-exit recovery proof passed');
} finally {
  const group = listenerGroup();
  if (group) { try { process.kill(-group, 'SIGTERM'); } catch {} }
  app.kill('SIGTERM');
  await Promise.race([
    new Promise((resolve) => app.once('exit', resolve)),
    new Promise((resolve) => setTimeout(resolve, 2_000))
  ]);
  if (app.exitCode === null) app.kill('SIGKILL');
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}
