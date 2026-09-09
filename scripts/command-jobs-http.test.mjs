import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import express from 'express';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { isInitializeRequest } from '@modelcontextprotocol/sdk/types.js';

import { createCodexProServer } from '../dist/server.js';

function testConfig(root) {
  return {
    defaultRoot: root,
    allowedRoots: [root],
    host: '127.0.0.1',
    port: 0,
    widgetDomain: '',
    requireHttpToken: false,
    allowQueryToken: false,
    allowSymlinks: false,
    corsOrigins: [],
    bashMode: 'full',
    writeMode: 'repository',
    toolMode: 'full',
    toolCardMode: 'off',
    codexDiagnosticReadMode: 'off',
    codexDiagnosticReadRequested: false,
    inheritEnv: true,
    maxReadBytes: 256_000,
    maxWriteBytes: 256_000,
    maxOutputBytes: 256_000,
    maxSearchResults: 200,
    maxHttpSessions: 8,
    httpSessionTtlMs: 60_000,
    blockedGlobs: [],
    contextDir: '.ai-bridge',
  };
}

async function startTestServer(config) {
  const app = express();
  const transports = new Map();
  app.use(express.json());
  app.post('/mcp', async (req, res) => {
    const sessionId = req.headers['mcp-session-id'];
    let transport = typeof sessionId === 'string' ? transports.get(sessionId) : undefined;
    if (!transport && !sessionId && isInitializeRequest(req.body)) {
      transport = new StreamableHTTPServerTransport({
        sessionIdGenerator: () => randomUUID(),
        onsessioninitialized: (newSessionId) => transports.set(newSessionId, transport),
      });
      const server = createCodexProServer(config, { commandSyncBudgetMs: 5 });
      await server.connect(transport);
    }
    if (!transport) {
      res.status(400).end();
      return;
    }
    await transport.handleRequest(req, res, req.body);
  });

  const httpServer = http.createServer(app);
  await new Promise((resolve, reject) => {
    httpServer.once('error', reject);
    httpServer.listen(0, '127.0.0.1', resolve);
  });
  const address = httpServer.address();
  assert.ok(address && typeof address === 'object');
  return {
    url: new URL(`http://127.0.0.1:${address.port}/mcp`),
    close: () => new Promise((resolve, reject) => httpServer.close((error) => error ? reject(error) : resolve())),
  };
}

async function connectClient(url) {
  const client = new Client({ name: 'command-jobs-http-test', version: '0.0.0' });
  await client.connect(new StreamableHTTPClientTransport(url));
  return client;
}

test('command_status retrieves a long command job through a distinct Streamable HTTP session', async () => {
  const root = await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-command-jobs-http-')));
  const server = await startTestServer(testConfig(root));
  const firstClient = await connectClient(server.url);
  const secondClient = await connectClient(server.url);

  try {
    const command = process.platform === 'win32'
      ? 'Start-Sleep -Milliseconds 150; Write-Output cross-session-complete'
      : 'sleep 0.15; printf cross-session-complete';
    const started = await firstClient.callTool({
      name: 'command',
      arguments: { command, timeout_ms: 1_000 },
    });
    assert.notEqual(started.isError, true);
    assert.equal(started.structuredContent.status, 'running');
    assert.match(started.structuredContent.job_id, /^[0-9a-f-]{36}$/i);

    const completed = await secondClient.callTool({
      name: 'command_status',
      arguments: { job_id: started.structuredContent.job_id, wait_ms: 1_000 },
    });
    assert.notEqual(completed.isError, true, completed.content?.[0]?.text);
    assert.equal(completed.structuredContent.status, 'completed');
    assert.equal(completed.structuredContent.exitCode, 0);
    assert.match(completed.structuredContent.stdout, /cross-session-complete/);
  } finally {
    await Promise.allSettled([firstClient.close(), secondClient.close()]);
    await server.close();
    await new Promise((resolve) => setTimeout(resolve, 500));
    await fs.rm(root, { recursive: true, force: true });
  }
});
