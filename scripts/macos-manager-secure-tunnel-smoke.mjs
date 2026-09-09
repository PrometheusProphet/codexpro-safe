import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

if (process.platform !== 'darwin') throw new Error('macOS secure-tunnel smoke test requires macOS.');

const root = fs.realpathSync(process.cwd());
const manager = path.join(root, 'artifacts/macos/CodexPro-Safe Manager.app/Contents/MacOS/CodexProSafeManager');
assert.ok(fs.statSync(manager).isFile(), 'Package the macOS Manager before running its secure-tunnel smoke test.');

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      assert.ok(address && typeof address === 'object');
      server.close(() => resolve(address.port));
    });
  });
}

const connectorPort = await freePort();
const tunnelPort = await freePort();
const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'codexpro-safe-manager-secure-tunnel-'));
const profileDirectory = path.join(temporaryRoot, 'profiles');
const settingsPath = path.join(temporaryRoot, 'settings.json');
const fakeClient = path.join(temporaryRoot, 'tunnel-client');
const tunnelID = 'tunnel_macSmoke123';
fs.mkdirSync(profileDirectory, { recursive: true, mode: 0o700 });
fs.writeFileSync(path.join(profileDirectory, 'codexpro-safe-local.yaml'), `tunnel_id: ${tunnelID}\n`, { mode: 0o600 });
fs.writeFileSync(fakeClient, `#!/usr/bin/env node
const http = require('node:http');
if (process.argv[2] === 'doctor') process.exit(process.env.CONTROL_PLANE_API_KEY ? 0 : 2);
if (process.argv[2] !== 'run') process.exit(64);
const address = process.env.HEALTH_LISTEN_ADDR || '127.0.0.1:8080';
const port = Number(address.slice(address.lastIndexOf(':') + 1));
const id = '${tunnelID}';
http.createServer((request, response) => {
  response.setHeader('content-type', 'application/json');
  if (request.url === '/healthz' || request.url === '/readyz') { response.end('{"ok":true}'); return; }
  if (request.url === '/api/status') {
    response.end(JSON.stringify({control_plane_tunnel_id:id,tunnel_metadata:{ID:id},channels:[{name:'main',probe_status:'ok'}]}));
    return;
  }
  response.statusCode = 404; response.end('{}');
}).listen(port, '127.0.0.1');
`, { mode: 0o700 });
fs.writeFileSync(settingsPath, JSON.stringify({
  repository: root,
  workspaceRoot: root,
  allowedRoot: path.dirname(root),
  nodePath: process.execPath,
  port: connectorPort,
  accessProfile: 'planning',
  tunnelMode: 'openai-secure',
  hostname: '',
  restartOnFailure: true,
  autoStartServices: true,
  tunnelClientPath: fakeClient,
  tunnelProfile: 'codexpro-safe-local',
  tunnelHealthPort: tunnelPort,
  organizationID: ''
}), { mode: 0o600 });

const app = spawn(manager, [], {
  cwd: root,
  env: {
    ...process.env,
    CI: '1',
    NO_COLOR: '1',
    CODEXPRO_MANAGER_SETTINGS: settingsPath,
    CODEXPRO_MANAGER_TEST_CONTROL_PLANE_API_KEY: 'synthetic-runtime-key',
    TUNNEL_CLIENT_PROFILE_DIR: profileDirectory
  },
  stdio: ['ignore', 'pipe', 'pipe']
});
let output = '';
app.stdout.on('data', (chunk) => { output += String(chunk); });
app.stderr.on('data', (chunk) => { output += String(chunk); });

function listenerGroup(port) {
  const listeners = spawnSync('/usr/sbin/lsof', ['-nP', `-iTCP:${port}`, '-sTCP:LISTEN', '-t'], { encoding: 'utf8' });
  const pid = Number(listeners.stdout.trim().split(/\s+/)[0]);
  if (!Number.isInteger(pid) || pid <= 0) return undefined;
  const group = Number(spawnSync('/bin/ps', ['-o', 'pgid=', '-p', String(pid)], { encoding: 'utf8' }).stdout.trim());
  return Number.isInteger(group) && group > 0 ? group : undefined;
}

async function waitForTunnel(excludedGroup) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (app.exitCode !== null) throw new Error(`Manager exited early (${app.exitCode}).\n${output.slice(-2_000)}`);
    const group = listenerGroup(tunnelPort);
    if (group && group !== excludedGroup) {
      try {
        const [health, ready, status] = await Promise.all([
          fetch(`http://127.0.0.1:${tunnelPort}/healthz`),
          fetch(`http://127.0.0.1:${tunnelPort}/readyz`),
          fetch(`http://127.0.0.1:${tunnelPort}/api/status`).then((response) => response.json())
        ]);
        if (health.ok && ready.ok && status.control_plane_tunnel_id === tunnelID &&
            status.tunnel_metadata.ID === tunnelID &&
            status.channels.some((channel) => channel.name === 'main' && channel.probe_status === 'ok')) return group;
      } catch {}
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Manager-owned secure tunnel did not become authenticated and ready.\n${output.slice(-2_000)}`);
}

async function verifyLocalToolCall() {
  const client = new Client({ name: 'macos-manager-secure-tunnel-smoke', version: '0.0.0' });
  try {
    await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${connectorPort}/mcp`)));
    const result = await client.callTool({ name: 'server_config', arguments: {} });
    assert.notEqual(result.isError, true, result.content?.[0]?.text);
    assert.equal(result.structuredContent.writeMode, 'handoff');
    assert.equal(result.structuredContent.bashMode, 'off');
  } finally {
    await client.close().catch(() => {});
  }
}

try {
  const firstTunnelGroup = await waitForTunnel();
  await verifyLocalToolCall();
  process.kill(-firstTunnelGroup, 'SIGKILL');
  const recoveredTunnelGroup = await waitForTunnel(firstTunnelGroup);
  assert.notEqual(recoveredTunnelGroup, firstTunnelGroup);
  await verifyLocalToolCall();
  console.log('✓ connector + authenticated secure-tunnel lifecycle and independent tunnel recovery proof passed');
} finally {
  for (const port of [tunnelPort, connectorPort]) {
    const group = listenerGroup(port);
    if (group) { try { process.kill(-group, 'SIGTERM'); } catch {} }
  }
  app.kill('SIGTERM');
  await Promise.race([
    new Promise((resolve) => app.once('exit', resolve)),
    new Promise((resolve) => setTimeout(resolve, 2_000))
  ]);
  if (app.exitCode === null) app.kill('SIGKILL');
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}
