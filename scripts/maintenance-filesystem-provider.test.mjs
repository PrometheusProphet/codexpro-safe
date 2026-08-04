import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  MAINTENANCE_FS_LIMITS,
  MAINTENANCE_FS_PROTOCOL,
  ManagedWindowsMaintenanceFsBoundary,
} from '../dist/windowsMaintenanceFsBoundary.js';

if (process.platform === 'win32') {
    const helper = path.resolve('tools/CodexProSafe.Manager/bin/CodexProSafe.DiagnosticHelper.exe');
    const helperHash = createHash('sha256').update(await fs.readFile(helper)).digest('hex');
    const manifest = JSON.parse((await fs.readFile(path.resolve('tools/CodexProSafe.Manager/bin/CodexProSafe.DiagnosticHelper.json'), 'utf8')).replace(/^\uFEFF/, ''));
    assert.deepEqual(Object.keys(manifest).sort(), ['executable', 'maintenanceFsProtocolVersion', 'protocolVersion', 'sha256']);
    assert.deepEqual(manifest, { protocolVersion: 'codexpro-diagnostic-v1', maintenanceFsProtocolVersion: MAINTENANCE_FS_PROTOCOL, executable: 'CodexProSafe.DiagnosticHelper.exe', sha256: helperHash });
    const installer = await fs.readFile(path.resolve('tools/CodexProSafe.Manager/install.ps1'), 'utf8');
    assert.match(installer, /CodexProSafe\.DiagnosticHelper\.json/);
    assert.throws(() => new ManagedWindowsMaintenanceFsBoundary({ executablePath: 'CodexProSafe.DiagnosticHelper.exe', expectedSha256: helperHash, expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: os.tmpdir() }), /path mismatch/);
    const originalRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-maintenance-fs-test-'));
    const fixtureDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-maintenance-client-fixture-'));
    let retainedRoot = originalRoot;
    try {
      await fs.mkdir(path.join(originalRoot, 'nested'));
      await fs.mkdir(path.join(originalRoot, 'duration'));
      await fs.writeFile(path.join(originalRoot, 'alpha.txt'), Buffer.from('alpha\r\nbeta\n', 'utf8'));
      await fs.writeFile(path.join(originalRoot, 'Zulu.txt'), Buffer.from('z', 'utf8'));
      await fs.writeFile(path.join(originalRoot, 'nested', 'binary.dat'), Buffer.from([65, 0, 66]));
      await fs.writeFile(path.join(originalRoot, 'nested', 'invalid.txt'), Buffer.from([0xc3, 0x28]));
      await fs.link(path.join(originalRoot, 'alpha.txt'), path.join(originalRoot, 'alpha-link.txt'));
      await Promise.all(Array.from({ length: 750 }, (_, index) => fs.writeFile(path.join(originalRoot, 'duration', `entry-${index.toString().padStart(4, '0')}.txt`), 'x')));
      await fs.symlink(path.join(originalRoot, 'nested'), path.join(originalRoot, 'junction'), 'junction');

      const boundary = new ManagedWindowsMaintenanceFsBoundary({ executablePath: helper, expectedSha256: helperHash, expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await boundary.ready();
      for (const invalid of [
        { maxDepth: 0 }, { maxDepth: 65 }, { maxDepth: 1.5 },
        { maxEntries: 0 }, { maxEntries: 4097 },
        { maxObservedBytes: -1 }, { maxObservedBytes: MAINTENANCE_FS_LIMITS.maxObservedBytes + 1 },
        { maxResponseBytes: 511 }, { maxResponseBytes: MAINTENANCE_FS_LIMITS.maxResponseBytes + 1 },
        { maxDurationMs: 0 }, { maxDurationMs: MAINTENANCE_FS_LIMITS.maxDurationMs + 1 },
      ]) assert.throws(() => boundary.walk(invalid), /integer mismatch/);
      for (const invalidId of ['', 's00000001e0000000', 's00000001e0000000g', '../file', 'C:\\file']) {
        assert.throws(() => boundary.hashFile(invalidId), /entry ID mismatch/);
      }

      retainedRoot = `${originalRoot}-retained`;
      await fs.rename(originalRoot, retainedRoot);
      await fs.mkdir(originalRoot);
      await fs.writeFile(path.join(originalRoot, 'decoy.txt'), 'decoy');

      const walk = await boundary.walk();
      assert.equal(walk.status, 'ok');
      assert.equal(walk.complete, true);
      assert.equal(walk.entries.some((entry) => entry.relativePath === 'decoy.txt'), false, 'retained root handle must remain authoritative');
      assert.equal(new Set(walk.entries.map((entry) => entry.entryId)).size, walk.entries.length);
      assert.deepEqual(walk.entries.map((entry) => entry.entryId), walk.entries.map((_, index) => `s00000001e${(index + 1).toString(16).padStart(8, '0')}`));
      assert.equal(walk.entries.every((entry) => !entry.relativePath.includes('\\') && !entry.relativePath.includes('..')), true);
      const junction = walk.entries.find((entry) => entry.relativePath === 'junction');
      assert.equal(junction?.kind, 'reparse');

      const alpha = walk.entries.find((entry) => entry.relativePath === 'alpha.txt');
      const binary = walk.entries.find((entry) => entry.relativePath === 'nested/binary.dat');
      const invalid = walk.entries.find((entry) => entry.relativePath === 'nested/invalid.txt');
      assert.ok(alpha && binary && invalid);
      assert.equal((await boundary.hashFile(alpha.entryId)).status, 'unavailable', 'multi-link file must be rejected');
      assert.equal((await boundary.readTextFile(binary.entryId)).status, 'not_text');
      assert.equal((await boundary.readTextFile(invalid.entryId)).status, 'not_text');

      await fs.unlink(path.join(retainedRoot, 'alpha-link.txt'));
      const refreshed = await boundary.walk();
      const refreshedAlpha = refreshed.entries.find((entry) => entry.relativePath === 'alpha.txt');
      assert.ok(refreshedAlpha, JSON.stringify(refreshed));
      assert.equal((await boundary.hashFile(alpha.entryId)).status, 'invalid_entry', 'new walk must invalidate old IDs');
      const text = await boundary.readTextFile(refreshedAlpha.entryId);
      assert.equal(Buffer.from(text.contentBase64, 'base64').toString('utf8'), 'alpha\r\nbeta\n');
      assert.equal(text.sha256, createHash('sha256').update(Buffer.from('alpha\r\nbeta\n')).digest('hex'));
      const hash = await boundary.hashFile(refreshedAlpha.entryId);
      assert.equal(hash.status, 'ok');
      assert.equal(hash.contentBase64, null);

      await fs.rename(path.join(retainedRoot, 'alpha.txt'), path.join(retainedRoot, 'alpha-old.txt'));
      await fs.writeFile(path.join(retainedRoot, 'alpha.txt'), 'replacement');
      assert.equal((await boundary.readTextFile(refreshedAlpha.entryId)).status, 'changed');

      const entryLimited = await boundary.walk({ maxEntries: 1 });
      assert.deepEqual([entryLimited.status, entryLimited.complete, entryLimited.limitation, entryLimited.entries.length], ['budget_exhausted', false, 'entries', 0]);
      const byteLimited = await boundary.walk({ maxObservedBytes: 0 });
      assert.equal(byteLimited.status, 'budget_exhausted');
      assert.equal(byteLimited.limitation, 'observed_bytes');
      const depthLimited = await boundary.walk({ maxDepth: 1 });
      assert.equal(depthLimited.status, 'budget_exhausted');
      assert.equal(depthLimited.limitation, 'depth');
      const responseLimited = await boundary.walk({ maxResponseBytes: 512 });
      assert.equal(responseLimited.status, 'budget_exhausted');
      assert.equal(responseLimited.limitation, 'response_bytes');
      const durationLimited = await boundary.walk({ maxDurationMs: 1 });
      assert.equal(durationLimited.status, 'budget_exhausted');
      assert.equal(durationLimited.complete, false);
      assert.equal(durationLimited.limitation, 'duration');
      await boundary.close();
      await boundary.close();

      const wrongFingerprint = new ManagedWindowsMaintenanceFsBoundary({ executablePath: helper, expectedSha256: '0'.repeat(64), expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await assert.rejects(wrongFingerprint.ready(), /fingerprint mismatch/);
      const wrongProtocol = new ManagedWindowsMaintenanceFsBoundary({ executablePath: helper, expectedSha256: helperHash, expectedProtocol: 'wrong', rootPath: originalRoot });
      await assert.rejects(wrongProtocol.ready(), /protocol mismatch/);

      const capturedOptions = { executablePath: helper, expectedSha256: helperHash, expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot };
      const capturedBoundary = new ManagedWindowsMaintenanceFsBoundary(capturedOptions);
      capturedOptions.expectedSha256 = '0'.repeat(64);
      capturedOptions.rootPath = 'C:\\missing-mutated-root';
      const savedCwd = process.cwd();
      process.chdir(os.tmpdir());
      try { await capturedBoundary.ready(); await capturedBoundary.close(); }
      finally { process.chdir(savedCwd); }

      const fixtureSource = path.resolve('scripts/fixtures/MaintenanceFsClientFixture.cs');
      for (const [name, define] of [['schema', 'SCHEMA'], ['version', 'VERSION'], ['oversized', 'OVERSIZED'], ['incomplete', 'INCOMPLETE'], ['concatenated', 'CONCATENATED'], ['unsolicited', 'UNSOLICITED']]) {
        const responseFixture = compileClientFixture(fixtureDirectory, fixtureSource, name, define);
        const responseBoundary = new ManagedWindowsMaintenanceFsBoundary({ executablePath: responseFixture, expectedSha256: await sha256File(responseFixture), expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
        const rejection = await responseBoundary.ready().then(() => null, (error) => error);
        assert.ok(rejection instanceof Error, `${name} response must fail`);
        assert.equal(String(rejection).includes(originalRoot), false, `${name} failure must not disclose root`);
        await responseBoundary.close().catch(() => undefined);
      }

      const mismatchFixture = compileClientFixture(fixtureDirectory, fixtureSource, 'mismatch', 'MISMATCH');
      const mismatchBoundary = new ManagedWindowsMaintenanceFsBoundary({ executablePath: mismatchFixture, expectedSha256: await sha256File(mismatchFixture), expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await mismatchBoundary.ready();
      await assert.rejects(mismatchBoundary.walk({ maxEntries: 1, maxObservedBytes: 1 }), /entry count mismatch|integer mismatch/);
      await mismatchBoundary.close().catch(() => undefined);

      const fileFixture = compileClientFixture(fixtureDirectory, fixtureSource, 'file', null);
      const fileBoundary = new ManagedWindowsMaintenanceFsBoundary({ executablePath: fileFixture, expectedSha256: await sha256File(fileFixture), expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await fileBoundary.ready();
      const fixtureWalk = await fileBoundary.walk({ maxEntries: 1, maxObservedBytes: 1 });
      await assert.rejects(fileBoundary.hashFile(fixtureWalk.entries[0].entryId, 1), /entry ID mismatch/);
      await fileBoundary.close().catch(() => undefined);

      const timeoutFixture = compileClientFixture(fixtureDirectory, fixtureSource, 'timeout', 'TIMEOUT');
      const timeoutBoundary = new ManagedWindowsMaintenanceFsBoundary({ executablePath: timeoutFixture, expectedSha256: await sha256File(timeoutFixture), expectedProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await assert.rejects(timeoutBoundary.ready(), /timeout/);
      await fs.rm(timeoutFixture, { force: true, maxRetries: 10, retryDelay: 100 });

      const bindRequest = { protocol: MAINTENANCE_FS_PROTOCOL, operation: 'bind_root', root: originalRoot };
      const rawFailures = [
        Buffer.from([0, 0, 0, 0]),
        Buffer.from([0x28, 0x23, 0, 0]),
        Buffer.concat([Buffer.from([10, 0, 0, 0]), Buffer.from('{}')]),
        framed({ ...bindRequest, extra: true }),
        framed({ protocol: MAINTENANCE_FS_PROTOCOL, operation: 'handshake' }),
        framedJson('{}{}'),
        framedJson(`{"protocol":"${MAINTENANCE_FS_PROTOCOL}","operation":"bind_root","root":"x","root":"y"}`),
        Buffer.concat([framed(bindRequest), framed({ protocol: MAINTENANCE_FS_PROTOCOL, operation: 'handshake' })]),
      ];
      for (const bytes of rawFailures) {
        const malformed = await runRawHelper(helper, bytes);
        assert.notEqual(malformed.code, 0);
        assert.equal(malformed.stderr.length, 0);
        assert.equal(malformed.stdout.includes(Buffer.from(originalRoot, 'utf8')), false);
      }
    } finally {
      await fs.rm(originalRoot, { recursive: true, force: true });
      if (retainedRoot !== originalRoot) await fs.rm(retainedRoot, { recursive: true, force: true });
      await fs.rm(fixtureDirectory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    }
}

  const sourceFiles = (await fs.readdir(path.resolve('src'))).filter((name) => name.endsWith('.ts') && name !== 'windowsMaintenanceFsBoundary.ts');
  for (const name of sourceFiles) assert.equal((await fs.readFile(path.resolve('src', name), 'utf8')).includes(MAINTENANCE_FS_PROTOCOL), false, `${name} must not register maintenance protocol`);
  const packageJson = JSON.parse(await fs.readFile(path.resolve('package.json'), 'utf8'));
  assert.equal(JSON.stringify(packageJson.bin).includes('maintenance'), false, 'no generic maintenance command may be exposed');
  const maintenanceSources = await Promise.all([
    fs.readFile(path.resolve('src/windowsMaintenanceFsBoundary.ts'), 'utf8'),
    fs.readFile(path.resolve('tools/CodexProSafe.Manager/DiagnosticHelper/MaintenanceProtocol.cs'), 'utf8'),
    fs.readFile(path.resolve('tools/CodexProSafe.Manager/DiagnosticHelper/MaintenanceFilesystemProvider.cs'), 'utf8'),
  ]);
  const joinedMaintenanceSources = maintenanceSources.join('\n');
  for (const prohibited of ['HttpListener', 'TcpListener', 'NamedPipeServerStream', 'ProcessStartInfo.Verb', 'runas', 'schtasks']) assert.equal(joinedMaintenanceSources.includes(prohibited), false, `${prohibited} must not expand privilege or service surface`);
console.log('maintenance filesystem provider tests passed');

function framed(value) { return framedJson(JSON.stringify(value)); }

function framedJson(json) {
  const body = Buffer.from(json, 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(body.length);
  return Buffer.concat([header, body]);
}

function runRawHelper(helper, bytes) {
  return new Promise((resolve, reject) => {
    const child = spawn(helper, ['--serve-maintenance-fs'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, shell: false });
    const stdout = [];
    const stderr = [];
    child.stdout.on('data', (chunk) => stdout.push(chunk));
    child.stderr.on('data', (chunk) => stderr.push(chunk));
    child.on('error', reject);
    child.on('exit', (code) => resolve({ code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr) }));
    child.stdin.end(bytes);
  });
}

function compileClientFixture(directory, source, name, define) {
  const framework = process.arch === 'x64' ? 'Framework64' : 'Framework';
  const compiler = path.join(process.env.WINDIR, 'Microsoft.NET', framework, 'v4.0.30319', 'csc.exe');
  const output = path.join(directory, `MaintenanceFsClientFixture-${name}.exe`);
  const args = ['/nologo', '/target:exe', `/out:${output}`, '/reference:System.dll', '/reference:System.Web.Extensions.dll'];
  if (define) args.push(`/define:${define}`);
  args.push(source);
  const result = spawnSync(compiler, args, { encoding: 'utf8' });
  assert.equal(result.status, 0, `fixture compilation failed: ${result.stdout}\n${result.stderr}`);
  return output;
}

async function sha256File(file) { return createHash('sha256').update(await fs.readFile(file)).digest('hex'); }
