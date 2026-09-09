import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

const allowedStages = new Set(['baseline', 'post-reboot', 'post-wake', 'soak']);
const argumentsList = process.argv.slice(2);
if (argumentsList.length === 1 && argumentsList[0] === '--help') {
  console.log('Usage: node scripts/macos-manager-live-certification.mjs --stage <baseline|post-reboot|post-wake|soak> [--installation <user|system>] [--duration-seconds 3600] [--required-recoveries 2]');
  process.exit(0);
}

const recognizedOptions = new Set(['--stage', '--installation', '--duration-seconds', '--required-recoveries']);
for (let index = 0; index < argumentsList.length; index += 2) {
  assert.ok(recognizedOptions.has(argumentsList[index]), `Unsupported option: ${argumentsList[index] ?? ''}`);
  assert.ok(index + 1 < argumentsList.length && !argumentsList[index + 1].startsWith('--'),
    `${argumentsList[index]} requires a value.`);
  assert.equal(argumentsList.indexOf(argumentsList[index]), index, `Duplicate option: ${argumentsList[index]}`);
}

function option(name, fallback) {
  const index = argumentsList.indexOf(name);
  if (index < 0) return fallback;
  assert.ok(index + 1 < argumentsList.length, `${name} requires a value.`);
  return argumentsList[index + 1];
}

if (process.platform !== 'darwin') throw new Error('Live macOS certification requires macOS.');
const stage = option('--stage', 'baseline');
assert.ok(allowedStages.has(stage), 'Unsupported certification stage.');
const installation = option('--installation', 'user');
assert.ok(['user', 'system'].includes(installation), 'Installation must be user or system.');
const durationSeconds = Number(option('--duration-seconds', stage === 'soak' ? '3600' : '0'));
const requiredRecoveries = Number(option('--required-recoveries', stage === 'soak' ? '2' : '0'));
assert.ok(Number.isInteger(durationSeconds) && durationSeconds >= 0 && durationSeconds <= 86_400,
  'Duration must be an integer from 0 through 86400 seconds.');
assert.ok(Number.isInteger(requiredRecoveries) && requiredRecoveries >= 0 && requiredRecoveries <= 20,
  'Required recoveries must be an integer from 0 through 20.');

const repoRoot = fs.realpathSync(process.cwd());
const managerApp = installation === 'system'
  ? '/Applications/CodexPro-Safe Manager.app'
  : path.join(os.homedir(), 'Applications/CodexPro-Safe Manager.app');
const managerBinary = path.join(managerApp, 'Contents/MacOS/CodexProSafeManager');
const settingsPath = path.join(os.homedir(), 'Library/Application Support/CodexProSafe Manager/settings.json');
const evidenceDirectory = path.join(repoRoot, '.ai-bridge');
const evidencePath = path.join(evidenceDirectory, `macos-manager-certification-${stage}.json`);

function fixedCommand(executable, args = []) {
  const result = spawnSync(executable, args, { encoding: 'utf8', maxBuffer: 1024 * 1024, timeout: 30_000 });
  if (result.status !== 0) throw new Error(`A bounded certification command failed: ${path.basename(executable)}.`);
  return result.stdout.trim();
}

function readBoundedText(file, maximumBytes, requirePrivateMode = false, allowedUIDs = [process.getuid()]) {
  const stat = fs.lstatSync(file);
  assert.ok(stat.isFile() && !stat.isSymbolicLink() && stat.nlink === 1 && allowedUIDs.includes(stat.uid) &&
    stat.size <= maximumBytes && fs.realpathSync(file) === path.resolve(file),
    'A certification input has unsafe object identity or size.');
  if (requirePrivateMode) assert.equal(stat.mode & 0o077, 0, 'A protected certification input is not private.');
  return fs.readFileSync(file, 'utf8');
}

function readBoundedJSON(file, maximumBytes) {
  return JSON.parse(readBoundedText(file, maximumBytes, true));
}

function processRows() {
  return fixedCommand('/bin/ps', ['-axo', 'pid=,ppid=,pgid=,command='])
    .split('\n')
    .map((line) => line.match(/^\s*(\d+)\s+(\d+)\s+(\d+)\s+(.*)$/))
    .filter(Boolean)
    .map((match) => ({ pid: Number(match[1]), ppid: Number(match[2]), pgid: Number(match[3]), command: match[4] }));
}

function listenerPids(port) {
  const result = spawnSync('/usr/sbin/lsof', ['-nP', `-iTCP:${port}`, '-sTCP:LISTEN', '-t'], {
    encoding: 'utf8', maxBuffer: 64 * 1024
  });
  if (result.status !== 0 && !result.stdout.trim()) return [];
  return [...new Set(result.stdout.trim().split(/\s+/).filter(Boolean).map(Number))];
}

function processStartEpoch(pid) {
  const value = Date.parse(fixedCommand('/bin/ps', ['-o', 'lstart=', '-p', String(pid)]));
  assert.ok(Number.isFinite(value), 'Could not establish bounded process start identity.');
  return Math.floor(value / 1000);
}

function verifyOwnedLifecycle(settings, requiredPreexistingEpoch = null) {
  const rows = processRows();
  const managers = rows.filter((row) => row.command === managerBinary);
  assert.equal(managers.length, 1, 'Expected exactly one installed Manager process.');
  const manager = managers[0];
  const connectorMarker = path.join(settings.repository, 'scripts/codexpro.mjs');
  const connectors = rows.filter((row) => row.ppid === manager.pid && row.pid === row.pgid &&
    row.command.includes(connectorMarker) && row.command.includes(' start ') && row.command.includes(' --tunnel none '));
  assert.equal(connectors.length, 1, 'Expected one isolated Manager-owned connector group.');
  const tunnelExpected = `${settings.tunnelClientPath} run --profile ${settings.tunnelProfile}`;
  const tunnels = rows.filter((row) => row.ppid === manager.pid && row.pid === row.pgid && row.command === tunnelExpected);
  assert.equal(tunnels.length, 1, 'Expected one isolated Manager-owned tunnel group.');
  const connectorListeners = listenerPids(settings.port);
  assert.ok(connectorListeners.length >= 1 && connectorListeners.every((pid) =>
    rows.some((row) => row.pid === pid && row.pgid === connectors[0].pid)),
  'Connector listener is not contained by the verified connector process group.');
  assert.deepEqual(listenerPids(settings.tunnelHealthPort), [tunnels[0].pid],
    'Tunnel health listener is not owned by the verified tunnel process.');
  if (requiredPreexistingEpoch !== null) {
    for (const process of [manager, connectors[0], tunnels[0]]) {
      assert.ok(processStartEpoch(process.pid) < requiredPreexistingEpoch,
        'A managed process did not predate the required power event.');
    }
  }
  return true;
}

async function boundedFetch(url, kind = 'text') {
  const response = await fetch(url, { headers: { 'cache-control': 'no-store' }, signal: AbortSignal.timeout(2_000) });
  assert.equal(response.status, 200, `Unexpected status from ${new URL(url).pathname}.`);
  const bytes = Buffer.from(await response.arrayBuffer());
  assert.ok(bytes.length <= 512 * 1024, 'Live status response exceeded the certification bound.');
  return kind === 'json' ? JSON.parse(bytes.toString('utf8')) : bytes.toString('utf8').trim();
}

async function verifyLocalToolCall(port) {
  const client = new Client({ name: 'macos-manager-live-certification', version: '1.0.0' });
  try {
    await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp`)));
    const result = await client.callTool({ name: 'server_config', arguments: {} });
    assert.notEqual(result.isError, true, 'The live local MCP tool call failed.');
    assert.equal(result.structuredContent?.writeMode, 'handoff');
    assert.equal(result.structuredContent?.bashMode, 'off');
    return { passed: true, writeMode: 'handoff', bashMode: 'off' };
  } finally {
    await client.close().catch(() => {});
  }
}

function expectedTunnelID(settings) {
  assert.match(settings.tunnelProfile, /^[A-Za-z0-9_.-]{1,100}$/);
  const profilePath = path.join(os.homedir(), '.config/tunnel-client', `${settings.tunnelProfile}.yaml`);
  const profile = readBoundedText(profilePath, 64 * 1024, true);
  const match = profile.match(/^\s*tunnel_id\s*:\s*["']?(tunnel_[A-Za-z0-9]+)["']?\s*$/m);
  assert.ok(match, 'Tunnel profile has no bounded tunnel identity.');
  return match[1];
}

async function liveSnapshot(settings, profileTunnelID, includeToolCall) {
  const connector = await boundedFetch(`http://127.0.0.1:${settings.port}/healthz`, 'json');
  assert.equal(connector.ok, true);
  const health = await boundedFetch(`http://127.0.0.1:${settings.tunnelHealthPort}/healthz`);
  const ready = await boundedFetch(`http://127.0.0.1:${settings.tunnelHealthPort}/readyz`);
  const tunnel = await boundedFetch(`http://127.0.0.1:${settings.tunnelHealthPort}/api/status`, 'json');
  const main = tunnel.channels?.find((channel) => channel.name === 'main');
  assert.equal(health, 'live');
  assert.equal(ready, 'ready');
  assert.equal(tunnel.control_plane_tunnel_id, profileTunnelID);
  assert.equal(tunnel.tunnel_metadata?.ID, profileTunnelID);
  assert.equal(main?.probe_status, 'ok');
  return {
    connectorHealthy: true,
    tunnelLive: true,
    tunnelReady: true,
    exactTunnelIdentity: true,
    mainProbe: 'ok',
    localTool: includeToolCall ? await verifyLocalToolCall(settings.port) : undefined
  };
}

const settings = readBoundedJSON(settingsPath, 64 * 1024);
assert.equal(settings.accessProfile, 'planning', 'Physical certification requires the Safe Planning profile.');
assert.equal(settings.tunnelMode, 'openai-secure', 'Physical certification requires the deliberate OpenAI tunnel mode.');
assert.equal(settings.restartOnFailure, true, 'Physical certification requires supervised recovery to be enabled.');
assert.ok(Number.isInteger(settings.port) && Number.isInteger(settings.tunnelHealthPort));
assert.ok(fs.realpathSync(settings.repository) === repoRoot, 'Manager repository does not match this checkout.');
assert.ok(fs.realpathSync(settings.workspaceRoot) === repoRoot, 'Manager workspace does not match this checkout.');
assert.ok(fs.existsSync(managerBinary), 'The installed macOS Manager is missing.');
readBoundedText(managerBinary, 64 * 1024 * 1024, false, [process.getuid(), 0]);
readBoundedText(settings.tunnelClientPath, 64 * 1024 * 1024);
console.log('checking installed app and protected state...');
const loginItem = fixedCommand(managerBinary, ['--login-item-status']);
const diagnosticHelper = fixedCommand(managerBinary, ['--diagnostic-helper-status']);
const controlPlaneKey = fixedCommand(managerBinary, ['--control-plane-key-status']);
assert.equal(diagnosticHelper, 'sealed');
assert.equal(controlPlaneKey, 'configured');
if (stage === 'baseline') {
  assert.equal(settings.autoStartServices, false, 'Baseline must preserve connector auto-start off.');
  assert.equal(loginItem, 'notRegistered', 'Baseline must preserve Launch at Login off.');
}
if (stage === 'post-reboot') {
  assert.equal(settings.autoStartServices, true, 'Post-reboot proof requires explicit connector auto-start opt-in.');
  assert.equal(loginItem, 'enabled', 'Post-reboot proof requires explicit Launch at Login opt-in.');
}

function kernelEpoch(name) {
  const description = fixedCommand('/usr/sbin/sysctl', ['-n', name]);
  const epoch = Number(description.match(/sec = (\d+)/)?.[1]);
  assert.ok(Number.isFinite(epoch), `Could not establish ${name}.`);
  return epoch;
}

const nowEpoch = Math.floor(Date.now() / 1000);
const bootEpoch = kernelEpoch('kern.boottime');
const bootAgeSeconds = Math.max(0, nowEpoch - bootEpoch);
if (stage === 'post-reboot') assert.ok(bootAgeSeconds <= 900, 'Post-reboot proof must run within 15 minutes of boot.');
let wakeEpoch = null;
let wakeAgeSeconds = null;
if (stage === 'post-wake') {
  wakeEpoch = kernelEpoch('kern.waketime');
  wakeAgeSeconds = Math.max(0, nowEpoch - wakeEpoch);
  assert.ok(wakeAgeSeconds <= 900, 'Post-wake proof must run within 15 minutes of a kernel-recorded wake.');
}

const profileTunnelID = expectedTunnelID(settings);
console.log('checking Manager-owned process groups and listeners...');
verifyOwnedLifecycle(settings, wakeEpoch);
console.log('checking live authenticated endpoints and local MCP call...');
const initial = await liveSnapshot(settings, profileTunnelID, true);
let samples = 1;
let readinessLosses = 0;
let recoveryTransitions = 0;
let previouslyReady = true;
const deadline = Date.now() + durationSeconds * 1_000;
while (Date.now() < deadline) {
  await new Promise((resolve) => setTimeout(resolve, Math.min(5_000, deadline - Date.now())));
  let readyNow = false;
  try {
    verifyOwnedLifecycle(settings);
    await liveSnapshot(settings, profileTunnelID, false);
    readyNow = true;
  } catch {
    readinessLosses += 1;
  }
  if (readyNow && !previouslyReady) recoveryTransitions += 1;
  previouslyReady = readyNow;
  samples += 1;
}
if (durationSeconds > 0) console.log('checking final recovery and MCP state...');
const final = await liveSnapshot(settings, profileTunnelID, true);
verifyOwnedLifecycle(settings);
assert.ok(recoveryTransitions >= requiredRecoveries,
  `Observed ${recoveryTransitions} recoveries; ${requiredRecoveries} required.`);

const sourceCommit = fixedCommand('/usr/bin/git', ['rev-parse', '--short=12', 'HEAD']);
const sourceClean = fixedCommand('/usr/bin/git', ['status', '--porcelain', '--untracked-files=no']) === '';
const evidence = {
  schema: 'codexpro-safe-macos-certification-v1',
  recordedAt: new Date().toISOString(),
  stage,
  sourceCommit,
  sourceClean,
  safeSettings: {
    accessProfile: 'planning',
    tunnelMode: 'openai-secure',
    restartOnFailure: true,
    autoStartServices: settings.autoStartServices === true
  },
  nativeState: { installation, loginItem, diagnosticHelper: 'sealed', controlPlaneKey: 'configured' },
  lifecycleOwnership: 'verified',
  initial,
  final,
  powerEvidence: { bootAgeSeconds, wakeAgeSeconds,
    processesPredatedWake: stage === 'post-wake' ? true : null },
  soak: { durationSeconds, samples, readinessLosses, recoveryTransitions, requiredRecoveries,
    certificationEligible: durationSeconds >= 3600 && recoveryTransitions >= 2 }
};
fs.mkdirSync(evidenceDirectory, { recursive: true, mode: 0o700 });
fs.writeFileSync(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`, { mode: 0o600 });
console.log(`✓ ${stage} live lifecycle, exact tunnel identity, and local MCP proof passed`);
console.log(`sanitized evidence: .ai-bridge/${path.basename(evidencePath)}`);
