import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

if (process.platform !== 'darwin') throw new Error('macOS Manager takeover smoke test requires macOS.');

const root = fs.realpathSync(process.cwd());
const allowedRoot = path.dirname(root);
const harness = path.join(root, 'tools/CodexProSafe.Manager.Mac/.build/debug/CodexProSafeTakeoverHarness');
const launcher = path.join(root, 'tools/CodexProSafe.Manager.Mac/.build/debug/CodexProSafeLauncher');
assert.ok(fs.statSync(harness).isFile(), 'Build the macOS Manager tests before takeover smoke.');
assert.ok(fs.statSync(launcher).isFile(), 'Build the macOS Manager before takeover smoke.');

async function freePort() {
  return await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      assert.ok(address && typeof address === 'object');
      server.close(() => resolve(address.port));
    });
  });
}

async function waitForHealth(port, expected) {
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    let healthy = false;
    try { healthy = await healthIsReady(port); } catch {}
    if (healthy === expected) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Health on port ${port} did not become ${expected ? 'ready' : 'stopped'}.`);
}

function healthIsReady(port) {
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

function stopGroup(child) {
  if (!child || child.exitCode !== null) return;
  const identity = spawnSync('/bin/ps', ['-o', 'pgid=', '-p', String(child.pid)], { encoding: 'utf8' });
  if (identity.status === 0 && identity.stdout.trim() === String(child.pid)) {
    try { process.kill(-child.pid, 'SIGTERM'); return; } catch {}
  }
  try { child.kill('SIGTERM'); } catch {}
}

const externalPort = await freePort();
const mismatchPort = await freePort();
const managerArguments = [path.join(root, 'scripts/codexpro.mjs'), 'start',
  '--root', root, '--allow-root', allowedRoot, '--port', String(externalPort),
  '--tunnel', 'none', '--mode', 'handoff', '--write', 'handoff', '--bash', 'off',
  '--no-copy-url', '--codex-diagnostic-read', 'off'];
const harnessArguments = [root, root, allowedRoot, process.execPath, String(externalPort), 'planning'];
let external;
let mismatch;
let managed;
let client;

try {
  mismatch = spawn(process.execPath, ['-e', `require('http').createServer((_,r)=>r.end('ok')).listen(${mismatchPort},'127.0.0.1')`],
    { detached: true, stdio: 'ignore' });
  await waitForHealth(mismatchPort, true);
  const refused = spawnSync(harness, ['stop', root, root, allowedRoot, process.execPath, String(mismatchPort), 'planning'],
    { cwd: root, encoding: 'utf8' });
  assert.notEqual(refused.status, 0, 'Mismatched listener must be refused.');
  assert.equal(await healthIsReady(mismatchPort), true,
    'Refused mismatched listener must remain untouched.');

  external = spawn(process.execPath, managerArguments, {
    cwd: root, detached: true, env: { ...process.env, NO_COLOR: '1', CI: '1' }, stdio: 'ignore'
  });
  await waitForHealth(externalPort, true);
  const takeover = spawnSync(harness, ['stop', ...harnessArguments], { cwd: root, encoding: 'utf8' });
  assert.equal(takeover.status, 0, `${takeover.stderr}\n${takeover.stdout}`);
  await waitForHealth(externalPort, false);

  managed = spawn(launcher, [process.execPath, ...managerArguments], {
    cwd: root, env: { ...process.env, NO_COLOR: '1', CI: '1' }, stdio: 'ignore'
  });
  await waitForHealth(externalPort, true);
  client = new Client({ name: 'macos-manager-takeover-smoke', version: '0.0.0' });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${externalPort}/mcp`)));
  const result = await client.callTool({ name: 'server_config', arguments: {} });
  assert.notEqual(result.isError, true, result.content?.[0]?.text);
  assert.equal(result.structuredContent.writeMode, 'handoff');
  assert.equal(result.structuredContent.bashMode, 'off');
  assert.equal(result.structuredContent.codexDiagnosticReadMode, 'off');
  console.log('✓ exact takeover, mismatch preservation, Manager relaunch, and real MCP proof passed');
} finally {
  await client?.close().catch(() => {});
  stopGroup(managed);
  stopGroup(external);
  stopGroup(mismatch);
}
