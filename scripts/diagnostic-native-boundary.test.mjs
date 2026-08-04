import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { DIAGNOSTIC_HELPER_PROTOCOL, ManagedWindowsDiagnosticBoundary } from '../dist/windowsDiagnosticBoundary.js';

const thisFile = fileURLToPath(import.meta.url);

if (process.argv[2] === '--fixture') {
  const mode = process.argv[3];
  let input = Buffer.alloc(0);
  process.stdin.on('data', (chunk) => {
    input = Buffer.concat([input, chunk]);
    if (input.length < 4) return;
    const length = input.readUInt32LE(0);
    if (input.length < length + 4) return;
    if (mode === 'crash') process.exit(7);
    if (mode === 'timeout') return;
    if (mode === 'truncated') {
      const header = Buffer.alloc(4);
      header.writeUInt32LE(49 * 1024 * 1024);
      process.stdout.write(header);
      return;
    }
    if (mode === 'partial') {
      const header = Buffer.alloc(4);
      header.writeUInt32LE(100);
      process.stdout.write(Buffer.concat([header, Buffer.from('{}')]));
      process.exit(0);
    }
    const response = mode === 'malformed'
      ? Buffer.from('{not-json', 'utf8')
      : Buffer.from(JSON.stringify({
          protocol: mode === 'wrong-version' ? 'wrong' : DIAGNOSTIC_HELPER_PROTOCOL,
          helperVersion: mode === 'wrong-version' ? 'wrong' : DIAGNOSTIC_HELPER_PROTOCOL,
          status: 'ok',
          entries: []
        }), 'utf8');
    const header = Buffer.alloc(4);
    header.writeUInt32LE(response.length);
    process.stdout.write(Buffer.concat([header, response]));
  });
} else {
  const fixtureHash = createHash('sha256').update(await fs.readFile(thisFile)).digest('hex');

  function fixture(mode, overrides = {}) {
    return new ManagedWindowsDiagnosticBoundary({
      executablePath: thisFile,
      expectedProtocol: DIAGNOSTIC_HELPER_PROTOCOL,
      expectedSha256: fixtureHash,
      commandForTest: process.execPath,
      argumentsForTest: [thisFile, '--fixture', mode],
      timeoutMsForTest: 150,
      ...overrides
    });
  }

  const ok = fixture('ok');
  assert.equal((await ok.inventory()).status, 'ok');
  ok.close();

  for (const mode of ['wrong-version', 'malformed', 'timeout', 'crash', 'truncated', 'partial']) {
    const boundary = fixture(mode);
    await assert.rejects(boundary.inventory(), undefined, mode);
    boundary.close();
  }

  const wrongFingerprint = fixture('ok', { expectedSha256: '0'.repeat(64) });
  await assert.rejects(wrongFingerprint.inventory(), undefined, 'wrong fingerprint');

  const missing = fixture('ok', { executablePath: path.join(path.dirname(thisFile), 'missing-helper.exe') });
  await new Promise((resolve) => setImmediate(resolve));
  await assert.rejects(missing.inventory(), undefined, 'missing helper');

  if (process.platform === 'win32') {
    const helper = path.resolve('tools/CodexProSafe.Manager/bin/CodexProSafe.DiagnosticHelper.exe');
    const helperHash = createHash('sha256').update(await fs.readFile(helper)).digest('hex');
    const actual = new ManagedWindowsDiagnosticBoundary({ executablePath: helper, expectedProtocol: DIAGNOSTIC_HELPER_PROTOCOL, expectedSha256: helperHash });
    await actual.ready();
    actual.close();
  }

  console.log('diagnostic native boundary tests passed');
}
