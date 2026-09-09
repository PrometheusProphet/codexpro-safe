import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

if (process.platform !== 'darwin') throw new Error('macOS Manager smoke test requires macOS.');

const root = fs.realpathSync(process.cwd());
const helper = path.join(root, 'tools/CodexProSafe.Manager.Mac/.build/debug/CodexProSafeLauncher');
assert.ok(fs.statSync(helper).isFile(), 'Build the macOS Manager before running its smoke test.');

const port = await new Promise((resolve, reject) => {
  const server = net.createServer();
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => {
    const address = server.address();
    assert.ok(address && typeof address === 'object');
    server.close(() => resolve(address.port));
  });
});

const child = spawn(helper, [process.execPath, path.join(root, 'scripts/codexpro.mjs'), 'start',
  '--root', root, '--allow-root', path.dirname(root), '--port', String(port),
  '--tunnel', 'none', '--mode', 'handoff', '--write', 'handoff', '--bash', 'off',
  '--no-copy-url', '--codex-diagnostic-read', 'off'], {
  cwd: root,
  env: { ...process.env, NO_COLOR: '1', CI: '1' },
  stdio: ['ignore', 'pipe', 'pipe']
});

let output = '';
child.stdout.on('data', (chunk) => { output += String(chunk); });
child.stderr.on('data', (chunk) => { output += String(chunk); });

async function waitForHealth() {
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/healthz`);
      if (response.ok) return;
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Manager-owned connector did not become healthy.\n${output.slice(-2_000)}`);
}

let client;
try {
  await waitForHealth();
  client = new Client({ name: 'macos-manager-smoke', version: '0.0.0' });
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
  const result = await client.callTool({ name: 'server_config', arguments: {} });
  assert.notEqual(result.isError, true, result.content?.[0]?.text);
  assert.equal(result.structuredContent.writeMode, 'handoff');
  assert.equal(result.structuredContent.bashMode, 'off');
  assert.equal(result.structuredContent.codexDiagnosticReadMode, 'off');
  console.log('✓ macOS Manager lifecycle, health, and real MCP tool-call proof passed');
} finally {
  await client?.close().catch(() => {});
  try { process.kill(-child.pid, 'SIGTERM'); } catch { try { child.kill('SIGTERM'); } catch {} }
  await Promise.race([
    new Promise((resolve) => child.once('exit', resolve)),
    new Promise((resolve) => setTimeout(resolve, 2_000))
  ]);
  if (child.exitCode === null) { try { process.kill(-child.pid, 'SIGKILL'); } catch {} }
}
