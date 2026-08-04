import assert from 'node:assert/strict';
import process from 'node:process';
import { loadConfig } from '../dist/config.js';
import { createCodexProServer, warmCodexDiagnosticBoundary } from '../dist/server.js';
import { MANAGER_LAUNCH_PROTOCOL, resolveWindowsManagerLaunchProof } from '../dist/windowsManagerLaunchProof.js';

const PIPE = 'codexpro-safe-diagnostic-0123456789abcdef0123456789abcdef';
const SYNTHETIC_HELPER = 'C:\\synthetic-manager-package\\CodexProSafe.DiagnosticHelper.exe';
const SYNTHETIC_HASH = 'b'.repeat(64);

function proof(overrides = {}) {
  const now = Date.now();
  return {
    protocol: MANAGER_LAUNCH_PROTOCOL,
    status: 'ok',
    instanceId: PIPE,
    managerPid: 101,
    launcherPid: 102,
    serverPid: 103,
    verifierPid: 104,
    issuedUtc: new Date(now - 100).toISOString(),
    expiresUtc: new Date(now + 5_000).toISOString(),
    helper: {
      executablePath: SYNTHETIC_HELPER,
      protocolVersion: 'codexpro-diagnostic-v1',
      sha256: SYNTHETIC_HASH
    },
    ...overrides
  };
}

function writeFrame(value) {
  const body = Buffer.from(JSON.stringify(value), 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length);
  process.stdout.write(Buffer.concat([header, body]));
}

if (process.argv[2] === '--fixture') {
  const mode = process.argv[3];
  const pipe = process.argv[4];
  if (mode === 'exit') process.exit(7);
  if (mode === 'oversized') {
    const header = Buffer.alloc(4);
    header.writeUInt32LE(5000);
    process.stdout.write(header);
    process.exit(0);
  }
  const value = proof({
    instanceId: mode === 'instance-mismatch' ? 'codexpro-safe-diagnostic-ffffffffffffffffffffffffffffffff' : pipe,
    ...(mode === 'stale' ? {
      issuedUtc: new Date(Date.now() - 20_000).toISOString(),
      expiresUtc: new Date(Date.now() - 10_000).toISOString()
    } : {})
  });
  writeFrame(value);
} else {
  const previous = new Map();
  const values = {
    CODEXPRO_DIAGNOSTIC_MANAGER_PIPE: PIPE,
    CODEXPRO_DIAGNOSTIC_MANAGER_GATE: 'codexpro-safe-diagnostic-gate-0123456789abcdef0123456789abcdef',
    CODEXPRO_DIAGNOSTIC_HELPER_PATH: SYNTHETIC_HELPER,
    CODEXPRO_DIAGNOSTIC_HELPER_VERSION: 'codexpro-diagnostic-v1',
    CODEXPRO_DIAGNOSTIC_HELPER_SHA256: SYNTHETIC_HASH
  };
  for (const [name, value] of Object.entries(values)) {
    previous.set(name, process.env[name]);
    process.env[name] = value;
  }
  try {
    const direct = loadConfig(['--root', process.cwd(), '--codex-diagnostic-read', 'read']);
    assert.equal(direct.codexDiagnosticReadRequested, true);
    assert.equal(direct.codexDiagnosticReadMode, 'off');
    assert.equal(direct.codexDiagnosticHelper, undefined, 'legacy helper environment must not establish trust');
    assert.deepEqual(
      Object.keys(createCodexProServer(direct)._registeredTools).filter((name) => name.startsWith('codex_diagnostic_')),
      [],
      'direct read request must not advertise diagnostics'
    );

    let helperStartupAttempted = false;
    await warmCodexDiagnosticBoundary(direct, {
      resolveLaunchProofForTest: async () => undefined,
      boundaryForTest: {
        async ready() { helperStartupAttempted = true; },
        close() {}
      }
    });
    assert.equal(helperStartupAttempted, false, 'helper startup must follow authenticated proof');
    assert.equal(direct.codexDiagnosticReadMode, 'off');

    const managed = loadConfig(['--root', process.cwd(), '--codex-diagnostic-read', 'read']);
    await warmCodexDiagnosticBoundary(managed, {
      resolveLaunchProofForTest: async () => proof(),
      boundaryForTest: {
        async ready() { helperStartupAttempted = true; },
        close() {}
      }
    });
    assert.equal(helperStartupAttempted, true);
    assert.equal(managed.codexDiagnosticReadMode, 'read');
    assert.equal(managed.codexDiagnosticHelper.sha256, SYNTHETIC_HASH);
    assert.deepEqual(
      Object.keys(createCodexProServer(managed)._registeredTools)
        .filter((name) => name.startsWith('codex_diagnostic_')).sort(),
      ['codex_diagnostic_config_summary', 'codex_diagnostic_inventory', 'codex_diagnostic_sqlite_metadata']
    );

    const rejectedHelper = loadConfig(['--root', process.cwd(), '--codex-diagnostic-read', 'read']);
    let closed = false;
    await warmCodexDiagnosticBoundary(rejectedHelper, {
      resolveLaunchProofForTest: async () => proof(),
      boundaryForTest: {
        async ready() { throw new Error('synthetic helper trust rejection'); },
        close() { closed = true; }
      }
    });
    assert.equal(closed, true);
    assert.equal(rejectedHelper.codexDiagnosticReadMode, 'off');
    assert.equal(rejectedHelper.codexDiagnosticHelper, undefined);

    const fixture = async (mode) => resolveWindowsManagerLaunchProof({
      pipeName: PIPE,
      commandForTest: process.execPath,
      argumentsForTest: [new URL(import.meta.url).pathname.slice(1), '--fixture', mode, PIPE],
      timeoutMsForTest: 1_000,
      capabilityForTest: 'c'.repeat(64)
    });
    assert.equal((await fixture('ok'))?.helper.sha256, SYNTHETIC_HASH);
    for (const mode of ['instance-mismatch', 'stale', 'oversized', 'exit']) {
      assert.equal(await fixture(mode), undefined, mode);
    }

    console.log('diagnostic manager launch tests passed');
  } finally {
    for (const [name, value] of previous) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
}
