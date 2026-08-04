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
const root = path.join(temp, '.codex');
await fs.mkdir(root);

try {
  const defaultConfig = loadConfig(['--root', temp]);
  assert.equal(defaultConfig.codexDiagnosticReadMode, 'off');
  assert.equal(defaultConfig.allowedRoots.includes(root), false);
  assert.throws(() => loadConfig(['--root', temp, '--codex-diagnostic-read', 'unsafe']));

  const offServer = createCodexProServer(defaultConfig);
  assert.equal(Object.keys(offServer._registeredTools).some((name) => name.startsWith('codex_diagnostic_')), false);
  const readServer = createCodexProServer(loadConfig(['--root', temp, '--codex-diagnostic-read', 'read']));
  assert.deepEqual(
    Object.keys(readServer._registeredTools).filter((name) => name.startsWith('codex_diagnostic_')).sort(),
    ['codex_diagnostic_config_summary', 'codex_diagnostic_inventory', 'codex_diagnostic_sqlite_metadata']
  );

  await fs.writeFile(path.join(root, 'config.toml'), '[general]\ntelemetry = true\nsecret = "token-should-not-leak"\nendpoint = "https://private.example"\n');
  await fs.mkdir(path.join(root, 'skills', 'synthetic-skill', '1.0.0'), { recursive: true });
  await fs.mkdir(path.join(root, 'plugins', 'synthetic-plugin', '1.0.0'), { recursive: true });
  await fs.mkdir(path.join(root, 'plugins', 'synthetic-plugin', '2.0.0'), { recursive: true });
  await fs.mkdir(path.join(root, 'sessions'));
  await fs.writeFile(path.join(root, 'sessions', 'transcript-private.jsonl'), 'do not expose');

  const SQL = await initSqlJs();
  const database = new SQL.Database();
  database.exec('CREATE TABLE private_events (created_at TEXT, payload TEXT); INSERT INTO private_events VALUES ("2026-01-01", "do-not-leak");');
  await fs.writeFile(path.join(root, 'logs_synthetic.sqlite'), Buffer.from(database.export()));
  database.close();
  await fs.writeFile(path.join(root, 'logs_synthetic.sqlite-wal'), 'sidecar');

  const beforeHash = createHash('sha256').update(await fs.readFile(path.join(root, 'logs_synthetic.sqlite'))).digest('hex');
  const diagnostics = new CodexDiagnosticOperations({ rootForTest: root });
  const inventory = await diagnostics.inventory();
  const inventoryText = JSON.stringify(inventory);
  assert.equal(inventory.status, 'ok');
  assert.equal(inventoryText.includes('transcript-private.jsonl'), false);
  assert.equal(inventoryText.includes(root), false);
  assert.equal(inventoryText.includes('logs_synthetic.sqlite'), true);
  assert.equal(inventoryText.includes('duplicate_version'), true);
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
  const afterHash = createHash('sha256').update(await fs.readFile(path.join(root, 'logs_synthetic.sqlite'))).digest('hex');
  assert.equal(afterHash, beforeHash, 'SQLite metadata operations must not modify runtime files');

  await fs.writeFile(path.join(root, 'logs_second.sqlite'), await fs.readFile(path.join(root, 'logs_synthetic.sqlite')));
  assert.equal((await diagnostics.sqliteMetadata('logs', 'summary')).status, 'ambiguous');
  await fs.rm(path.join(root, 'logs_second.sqlite'));

  const external = path.join(temp, 'external');
  await fs.mkdir(external);
  await fs.rm(path.join(root, 'skills'), { recursive: true });
  try {
    await fs.symlink(external, path.join(root, 'skills'), process.platform === 'win32' ? 'junction' : 'dir');
    assert.equal((await diagnostics.inventory()).status, 'unavailable');
  } catch (error) {
    if (error?.code !== 'EPERM') throw error;
  }

  console.log('diagnostic-read tests passed');
} finally {
  await fs.rm(temp, { recursive: true, force: true });
}
