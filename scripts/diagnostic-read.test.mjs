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

  const external = path.join(temp, 'external');
  await fs.mkdir(path.join(external, 'outside-skill', 'outside-version'), { recursive: true });
  const skillsPath = path.join(root, 'skills');
  const skillsBackup = path.join(root, 'skills-before-race');
  let extensionSwapComplete = false;
  const racedExtensions = new CodexDiagnosticOperations({
    rootForTest: root,
    beforeExtensionEnumerationForTest: async (category) => {
      if (category !== 'skills' || extensionSwapComplete) return;
      extensionSwapComplete = true;
      await fs.rename(skillsPath, skillsBackup);
      await fs.symlink(external, skillsPath, process.platform === 'win32' ? 'junction' : 'dir');
    }
  });
  const extensionRace = await racedExtensions.inventory();
  const extensionRaceText = JSON.stringify(extensionRace);
  assert.equal(extensionRace.status, 'unavailable');
  assert.equal(extensionRaceText.includes('outside-skill'), false);
  assert.equal(extensionRaceText.includes('outside-version'), false);
  await fs.rm(skillsPath, { recursive: true, force: true });
  await fs.rename(skillsBackup, skillsPath);

  const configPath = path.join(root, 'config.toml');
  const configBackup = path.join(root, 'config-before-race.toml');
  const replacementConfig = path.join(external, 'replacement.toml');
  await fs.writeFile(replacementConfig, '[general]\ntelemetry = false\n');
  let configSwapComplete = false;
  const racedConfiguration = new CodexDiagnosticOperations({
    rootForTest: root,
    beforeFixedFileReadForTest: async (kind) => {
      if (kind !== 'configuration' || configSwapComplete) return;
      configSwapComplete = true;
      await fs.rename(configPath, configBackup);
      await fs.copyFile(replacementConfig, configPath);
    }
  });
  const configurationRace = await racedConfiguration.configurationSummary();
  assert.equal(configurationRace.status, 'unavailable');
  assert.equal(JSON.stringify(configurationRace).includes('false'), false);
  await fs.rm(configPath);
  await fs.rename(configBackup, configPath);

  const databasePath = path.join(root, 'logs_synthetic.sqlite');
  const databaseBackup = path.join(root, 'logs-before-race.sqlite');
  const replacementDatabase = path.join(external, 'replacement.sqlite');
  await fs.copyFile(databasePath, replacementDatabase);
  let databaseSwapComplete = false;
  const racedDatabase = new CodexDiagnosticOperations({
    rootForTest: root,
    beforeFixedFileReadForTest: async (kind) => {
      if (kind !== 'database' || databaseSwapComplete) return;
      databaseSwapComplete = true;
      await fs.rename(databasePath, databaseBackup);
      await fs.copyFile(replacementDatabase, databasePath);
    }
  });
  const databaseRace = await racedDatabase.sqliteMetadata('logs', 'row_counts');
  assert.equal(databaseRace.status, 'unavailable');
  assert.equal(JSON.stringify(databaseRace).includes('logs_synthetic.sqlite'), false);
  await fs.rm(databasePath);
  await fs.rename(databaseBackup, databasePath);

  await fs.writeFile(path.join(root, 'logs_second.sqlite'), await fs.readFile(path.join(root, 'logs_synthetic.sqlite')));
  assert.equal((await diagnostics.sqliteMetadata('logs', 'summary')).status, 'ambiguous');
  await fs.rm(path.join(root, 'logs_second.sqlite'));

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
