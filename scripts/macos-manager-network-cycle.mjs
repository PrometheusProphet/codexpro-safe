import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const args = process.argv.slice(2);
assert.ok(args.length === 0 || (args.length === 2 && args[0] === '--off-seconds'),
  'Usage: node scripts/macos-manager-network-cycle.mjs [--off-seconds 45]');
const offSeconds = Number(args.length === 2 ? args[1] : 45);
assert.ok(Number.isInteger(offSeconds) && offSeconds >= 10 && offSeconds <= 120,
  'Off duration must be an integer from 10 through 120 seconds.');
if (process.platform !== 'darwin') throw new Error('Network-cycle proof requires macOS.');

const repoRoot = fs.realpathSync(process.cwd());
const eventPath = path.join(repoRoot, '.ai-bridge/macos-manager-network-events.json');

function command(executable, commandArgs) {
  const result = spawnSync(executable, commandArgs, { encoding: 'utf8', timeout: 30_000, maxBuffer: 1024 * 1024 });
  if (result.status !== 0) throw new Error(`A bounded network-cycle command failed: ${path.basename(executable)}.`);
  return result.stdout.trim();
}

function readEvents() {
  const stat = fs.lstatSync(eventPath);
  assert.ok(stat.isFile() && !stat.isSymbolicLink() && stat.nlink === 1 && stat.uid === process.getuid() &&
    stat.size <= 64 * 1024 && (stat.mode & 0o077) === 0 && fs.realpathSync(eventPath) === eventPath,
  'Network-event ledger has unsafe object identity, access, or size.');
  const value = JSON.parse(fs.readFileSync(eventPath, 'utf8'));
  assert.equal(value.schema, 'codexpro-safe-macos-network-events-v1');
  assert.equal(value.status, 'active');
  assert.match(value.runID, /^[0-9a-f-]{36}$/i);
  assert.ok(Array.isArray(value.cycles) && value.cycles.length < 20);
  return value;
}

function readSettings() {
  const settingsPath = path.join(os.homedir(), 'Library/Application Support/CodexProSafe Manager/settings.json');
  const stat = fs.lstatSync(settingsPath);
  assert.ok(stat.isFile() && !stat.isSymbolicLink() && stat.nlink === 1 && stat.uid === process.getuid() &&
    stat.size <= 64 * 1024 && (stat.mode & 0o077) === 0 && fs.realpathSync(settingsPath) === settingsPath,
  'Manager settings have unsafe object identity, access, or size.');
  return JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
}

function wifiInterface() {
  const output = command('/usr/sbin/networksetup', ['-listallhardwareports']);
  const match = output.match(/Hardware Port: Wi-Fi\nDevice: (en\d+)/);
  assert.ok(match, 'No bounded Wi-Fi interface was found.');
  return match[1];
}

async function metrics(port) {
  const response = await fetch(`http://127.0.0.1:${port}/metrics`, { signal: AbortSignal.timeout(2_000) });
  assert.equal(response.status, 200);
  const body = await response.text();
  assert.ok(Buffer.byteLength(body) <= 512 * 1024);
  const errors = [...body.matchAll(/^commands_poll_errors_total(?:\{[^}]*\})?\s+([0-9.eE+-]+)$/gm)]
    .reduce((sum, match) => sum + Number(match[1]), 0);
  const success = Number(body.match(/^commands_poll_last_successful_timestamp_seconds(?:\{[^}]*\})?\s+([0-9.eE+-]+)$/m)?.[1]);
  assert.ok(Number.isFinite(errors) && Number.isFinite(success));
  return { errors, success };
}

const settings = readSettings();
assert.ok(Number.isInteger(settings.tunnelHealthPort));
const events = readEvents();
const interfaceName = wifiInterface();
assert.equal(command('/usr/sbin/networksetup', ['-getairportpower', interfaceName]).split(/\s+/).at(-1), 'On');
const before = await metrics(settings.tunnelHealthPort);
const startedAt = new Date().toISOString();
let wifiRestored = false;
function restore() {
  if (wifiRestored) return;
  const result = spawnSync('/usr/sbin/networksetup', ['-setairportpower', interfaceName, 'on'], { timeout: 30_000 });
  wifiRestored = result.status === 0;
  if (!wifiRestored) throw new Error('Wi-Fi restoration failed.');
}
for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.once(signal, () => {
  try { restore(); } finally { process.exit(128); }
});

try {
  console.log(`network_cycle_${events.cycles.length + 1}=off`);
  command('/usr/sbin/networksetup', ['-setairportpower', interfaceName, 'off']);
  await new Promise((resolve) => setTimeout(resolve, offSeconds * 1_000));
} finally {
  restore();
}
console.log(`network_cycle_${events.cycles.length + 1}=on`);

let after;
const deadline = Date.now() + 120_000;
while (Date.now() < deadline) {
  try {
    after = await metrics(settings.tunnelHealthPort);
    const recent = Date.now() / 1000 - after.success <= 45;
    if (after.errors > before.errors && after.success > before.success && recent) break;
  } catch {}
  await new Promise((resolve) => setTimeout(resolve, 2_000));
}
assert.ok(after && after.errors > before.errors, 'No new control-plane poll error followed the Wi-Fi outage.');
assert.ok(after.success > before.success && Date.now() / 1000 - after.success <= 45,
  'No recent successful control-plane poll followed the Wi-Fi outage.');

const current = readEvents();
assert.equal(current.runID, events.runID, 'The active soak changed during the network cycle.');
assert.equal(current.cycles.length, events.cycles.length, 'Another network cycle updated the ledger concurrently.');
current.cycles.push({ index: current.cycles.length + 1, startedAt, recoveredAt: new Date().toISOString(),
  offSeconds, pollErrorsDelta: after.errors - before.errors, successfulPollAdvanced: true });
const temporaryPath = `${eventPath}.${process.pid}.tmp`;
assert.ok(!fs.existsSync(temporaryPath));
fs.writeFileSync(temporaryPath, `${JSON.stringify(current, null, 2)}\n`, { mode: 0o600 });
fs.renameSync(temporaryPath, eventPath);
console.log(`network_cycle_${current.cycles.length}=recovered`);
