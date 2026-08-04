import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import initSqlJs from 'sql.js';
import { loadConfig } from '../dist/config.js';
import { createCodexProServer } from '../dist/server.js';
import { CodexDiagnosticOperations } from '../dist/diagnosticOps.js';

const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-safe-diagnostic-'));

try {
  const defaultConfig = loadConfig(['--root', temp]);
  assert.equal(defaultConfig.codexDiagnosticReadMode, 'off');
  assert.equal(defaultConfig.codexDiagnosticReadRequested, false);
  assert.equal(defaultConfig.allowedRoots.includes(path.join(temp, '.codex')), false);
  assert.throws(() => loadConfig(['--root', temp, '--codex-diagnostic-read', 'unsafe']));

  const offServer = createCodexProServer(defaultConfig);
  assert.equal(Object.keys(offServer._registeredTools).some((name) => name.startsWith('codex_diagnostic_')), false);
  const requestedReadConfig = loadConfig(['--root', temp, '--codex-diagnostic-read', 'read']);
  assert.equal(requestedReadConfig.codexDiagnosticReadRequested, true);
  assert.equal(requestedReadConfig.codexDiagnosticReadMode, 'off');
  const requestedReadServer = createCodexProServer(requestedReadConfig);
  assert.equal(Object.keys(requestedReadServer._registeredTools).some((name) => name.startsWith('codex_diagnostic_')), false);
  const readServer = createCodexProServer({
    ...requestedReadConfig,
    codexDiagnosticReadMode: 'read',
    codexDiagnosticHelper: {
      executablePath: 'C:\\synthetic-manager-package\\CodexProSafe.DiagnosticHelper.exe',
      protocolVersion: 'codexpro-diagnostic-v1',
      sha256: 'b'.repeat(64)
    }
  });
  assert.deepEqual(
    Object.keys(readServer._registeredTools).filter((name) => name.startsWith('codex_diagnostic_')).sort(),
    ['codex_diagnostic_config_summary', 'codex_diagnostic_inventory', 'codex_diagnostic_sqlite_metadata']
  );
  assert.deepEqual(Object.keys(readServer._registeredTools.codex_diagnostic_inventory.inputSchema.shape), []);
  assert.deepEqual(Object.keys(readServer._registeredTools.codex_diagnostic_config_summary.inputSchema.shape), []);
  assert.deepEqual(
    Object.keys(readServer._registeredTools.codex_diagnostic_sqlite_metadata.inputSchema.shape).sort(),
    ['family_kind', 'operation']
  );

  assert.equal((await new CodexDiagnosticOperations().inventory()).status, 'unavailable');
  assert.equal((await new CodexDiagnosticOperations().configurationSummary()).status, 'unavailable');
  assert.equal((await new CodexDiagnosticOperations().sqliteMetadata('logs', 'summary')).status, 'unavailable');

  const configBytes = Buffer.from('[general]\ntelemetry = true\nsecret = "token-should-not-leak"\nendpoint = "https://private.example"\n');
  const SQL = await initSqlJs();
  const sqlite = new SQL.Database();
  sqlite.exec('CREATE TABLE private_events (created_at TEXT, payload TEXT); INSERT INTO private_events VALUES ("2026-01-01", "do-not-leak");');
  const databaseBytes = Buffer.from(sqlite.export());
  sqlite.close();
  const beforeConfigHash = createHash('sha256').update(configBytes).digest('hex');
  const beforeDatabaseHash = createHash('sha256').update(databaseBytes).digest('hex');
  const timestamp = '2026-08-04T00:00:00.000Z';

  const boundary = {
    async inventory() {
      return {
        status: 'ok',
        entries: [
          { name: 'config.toml', isDirectory: false, isReparsePoint: false, bytes: configBytes.length, modifiedUtc: timestamp },
          { name: 'skills', isDirectory: true, isReparsePoint: false, bytes: 0, modifiedUtc: timestamp },
          { name: 'plugins', isDirectory: true, isReparsePoint: false, bytes: 0, modifiedUtc: timestamp },
          { name: 'sessions', isDirectory: true, isReparsePoint: false, bytes: 0, modifiedUtc: timestamp },
          { name: 'logs_synthetic.sqlite', isDirectory: false, isReparsePoint: false, bytes: databaseBytes.length, modifiedUtc: timestamp },
          { name: 'logs_synthetic.sqlite-wal', isDirectory: false, isReparsePoint: false, bytes: 4, modifiedUtc: timestamp }
        ]
      };
    },
    async configuration() {
      return {
        status: 'ok',
        files: [
          { name: 'config.toml', status: 'present', bytes: configBytes.length, modifiedUtc: timestamp, contentBase64: configBytes.toString('base64') },
          { name: 'config.toml.bak', status: 'absent', bytes: 0, modifiedUtc: timestamp },
          { name: 'config.toml.backup', status: 'absent', bytes: 0, modifiedUtc: timestamp }
        ]
      };
    },
    async database(familyKind) {
      if (familyKind !== 'logs') return { status: 'missing' };
      return {
        status: 'ok',
        database: { name: 'logs_synthetic.sqlite', bytes: databaseBytes.length, modifiedUtc: timestamp, contentBase64: databaseBytes.toString('base64') },
        sidecars: ['wal']
      };
    },
    close() {}
  };

  const diagnostics = new CodexDiagnosticOperations({ boundaryForTest: boundary });
  const inventory = await diagnostics.inventory();
  const inventoryText = JSON.stringify(inventory);
  assert.equal(inventory.status, 'ok');
  assert.equal(inventoryText.includes('logs_synthetic.sqlite'), true);
  assert.deepEqual(inventory.installed_extensions, { status: 'unavailable' });
  for (const forbidden of ['transcript-private.jsonl', 'synthetic-skill', 'synthetic-plugin', 'outside-version', 'duplicate_version', '1.0.0']) {
    assert.equal(inventoryText.includes(forbidden), false, forbidden);
  }
  assert.equal(inventory.process_locks.status, 'unavailable');

  const configuration = await diagnostics.configurationSummary();
  const configurationText = JSON.stringify(configuration);
  assert.equal(configuration.status, 'ok');
  assert.equal(configurationText.includes('token-should-not-leak'), false);
  assert.equal(configurationText.includes('private.example'), false);
  assert.equal(configuration.files[0].approved['general.telemetry'], true);
  assert.equal(configuration.files[0].omitted_items >= 2, true);

  for (const operation of ['summary', 'integrity_check', 'storage_metadata', 'schema', 'row_counts', 'timestamp_ranges']) {
    const value = await diagnostics.sqliteMetadata('logs', operation);
    const serialized = JSON.stringify(value);
    assert.equal(value.status, 'ok', operation);
    assert.equal(serialized.includes('do-not-leak'), false, operation);
    assert.equal(serialized.includes('private_events'), false, operation);
    assert.equal(serialized.includes('created_at'), false, operation);
  }
  assert.equal(createHash('sha256').update(configBytes).digest('hex'), beforeConfigHash);
  assert.equal(createHash('sha256').update(databaseBytes).digest('hex'), beforeDatabaseHash);

  const delayedWorker = new CodexDiagnosticOperations({ boundaryForTest: boundary, sqliteWorkerDelayMsForTest: 10_000 });
  const delayedStarted = Date.now();
  assert.equal((await delayedWorker.sqliteMetadata('logs', 'summary')).status, 'unavailable');
  assert.equal(Date.now() - delayedStarted < 3_500, true, 'SQLite worker must be terminated by the wall-clock budget');

  const ambiguous = new CodexDiagnosticOperations({
    boundaryForTest: { ...boundary, async database() { return { status: 'ambiguous', matches: 2 }; } }
  });
  assert.equal((await ambiguous.sqliteMetadata('logs', 'summary')).status, 'ambiguous');

  const oversized = new CodexDiagnosticOperations({
    boundaryForTest: {
      ...boundary,
      async inventory() {
        return { status: 'ok', entries: [{ name: 'logs_large.sqlite', isDirectory: false, isReparsePoint: false, bytes: 33 * 1024 * 1024, modifiedUtc: timestamp }] };
      },
      async database() {
        return { status: 'oversized', database: { name: 'logs_large.sqlite', bytes: 33 * 1024 * 1024, modifiedUtc: timestamp }, sidecars: [] };
      }
    }
  });
  assert.equal(JSON.stringify(await oversized.inventory()).includes('logs_large.sqlite'), true);
  assert.equal((await oversized.sqliteMetadata('logs', 'summary')).status, 'unavailable');

  const externalMetadata = new CodexDiagnosticOperations({
    boundaryForTest: {
      ...boundary,
      async inventory() {
        return { status: 'ok', entries: [{ name: 'skills', isDirectory: true, isReparsePoint: true, bytes: 0, modifiedUtc: timestamp }] };
      }
    }
  });
  const externalInventory = await externalMetadata.inventory();
  assert.equal(externalInventory.status, 'ok');
  assert.deepEqual(externalInventory.installed_extensions, { status: 'unavailable' });
  assert.equal(JSON.stringify(externalInventory).includes('outside-skill'), false);

  console.log('diagnostic-read tests passed');
} finally {
  await fs.rm(temp, { recursive: true, force: true });
}
