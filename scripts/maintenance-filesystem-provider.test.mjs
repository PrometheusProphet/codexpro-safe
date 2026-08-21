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
    const helper = await fs.realpath(path.resolve('tools/CodexProSafe.Manager/bin/CodexProSafe.DiagnosticHelper.exe'));
    const launcher = await fs.realpath(path.resolve('tools/CodexProSafe.Manager/bin/CodexProSafe.MaintenanceFsLauncher.exe'));
    const manifestPath = await fs.realpath(path.resolve('tools/CodexProSafe.Manager/bin/CodexProSafe.DiagnosticHelper.json'));
    const helperHash = createHash('sha256').update(await fs.readFile(helper)).digest('hex');
    const manifestBytes = await fs.readFile(manifestPath);
    const manifestHash = createHash('sha256').update(manifestBytes).digest('hex');
    const manifest = JSON.parse(manifestBytes.toString('utf8').replace(/^\uFEFF/, ''));
    assert.deepEqual(Object.keys(manifest).sort(), ['executable', 'maintenanceFsProtocolVersion', 'protocolVersion', 'sha256']);
    assert.deepEqual(manifest, { protocolVersion: 'codexpro-diagnostic-v1', maintenanceFsProtocolVersion: MAINTENANCE_FS_PROTOCOL, executable: 'CodexProSafe.DiagnosticHelper.exe', sha256: helperHash });
    const installer = await fs.readFile(path.resolve('tools/CodexProSafe.Manager/install.ps1'), 'utf8');
    assert.match(installer, /CodexProSafe\.DiagnosticHelper\.json/);
    assert.equal(installer.includes('CodexProSafe.MaintenanceFsLauncher.exe'), false, 'synthetic launcher must not enter Manager installation');
    assert.throws(() => new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: 'CodexProSafe.MaintenanceFsLauncher.exe', manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: os.tmpdir() }), /path mismatch/);
    const originalRoot = await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-maintenance-fs-test-')));
    const fixtureDirectory = await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-maintenance-client-fixture-')));
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

      const boundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: launcher, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
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

      const unsupportedRoot = path.join(originalRoot, 'unsupported-root-junction');
      await fs.symlink(retainedRoot, unsupportedRoot, 'junction');
      const unsupportedHelper = await runHelperRequest(helper, framed({ protocol: MAINTENANCE_FS_PROTOCOL, operation: 'bind_root', root: unsupportedRoot }));
      assert.deepEqual(parseFirstFrame(unsupportedHelper.stdout), { protocol: MAINTENANCE_FS_PROTOCOL, operation: 'bind_root', status: 'unsupported' });
      assert.equal(unsupportedHelper.stderr.length, 0);
      const unsupportedBoundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: launcher, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: unsupportedRoot });
      await assert.rejects(unsupportedBoundary.ready(), /response mismatch/);
      await unsupportedBoundary.close().catch(() => undefined);

      const wrongFingerprint = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: launcher, manifestPath, expectedManifestSha256: '0'.repeat(64), expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await assert.rejects(wrongFingerprint.ready(), /exited|unavailable/);
      const wrongProtocol = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: launcher, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: 'wrong', rootPath: originalRoot });
      await assert.rejects(wrongProtocol.ready(), /protocol mismatch/);

      const capturedOptions = { trustedLauncherPath: launcher, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot };
      const capturedBoundary = new ManagedWindowsMaintenanceFsBoundary(capturedOptions);
      capturedOptions.expectedManifestSha256 = '0'.repeat(64);
      capturedOptions.rootPath = 'C:\\missing-mutated-root';
      const savedCwd = process.cwd();
      process.chdir(os.tmpdir());
      try { await capturedBoundary.ready(); await capturedBoundary.close(); }
      finally { process.chdir(savedCwd); }

      const fixtureSource = path.resolve('scripts/fixtures/MaintenanceFsClientFixture.cs');
      for (const [name, define] of [['schema', 'SCHEMA'], ['version', 'VERSION'], ['oversized', 'OVERSIZED'], ['incomplete', 'INCOMPLETE'], ['concatenated', 'CONCATENATED'], ['unsolicited', 'UNSOLICITED']]) {
        const responseFixture = compileClientFixture(fixtureDirectory, fixtureSource, name, define);
        const responseBoundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: responseFixture, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
        const rejection = await responseBoundary.ready().then(() => null, (error) => error);
        assert.ok(rejection instanceof Error, `${name} response must fail`);
        assert.equal(String(rejection).includes(originalRoot), false, `${name} failure must not disclose root`);
        await responseBoundary.close().catch(() => undefined);
      }

      const mismatchFixture = compileClientFixture(fixtureDirectory, fixtureSource, 'mismatch', 'MISMATCH');
      const mismatchBoundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: mismatchFixture, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await mismatchBoundary.ready();
      await assert.rejects(mismatchBoundary.walk({ maxEntries: 1, maxObservedBytes: 1 }), /entry count mismatch|integer mismatch/);
      await mismatchBoundary.close().catch(() => undefined);

      const fileFixture = compileClientFixture(fixtureDirectory, fixtureSource, 'file', null);
      const fileBoundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: fileFixture, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
      await fileBoundary.ready();
      const fixtureWalk = await fileBoundary.walk({ maxEntries: 1, maxObservedBytes: 1 });
      await assert.rejects(fileBoundary.hashFile(fixtureWalk.entries[0].entryId, 1), /entry ID mismatch/);
      await fileBoundary.close().catch(() => undefined);

      const timeoutFixture = compileClientFixture(fixtureDirectory, fixtureSource, 'timeout', 'TIMEOUT');
      const timeoutBoundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: timeoutFixture, manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: originalRoot });
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

      const launcherBootstrap = { protocol: 'codexpro-maintenance-fs-launcher-v1', operation: 'bootstrap', manifestPath, expectedManifestSha256: manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, root: originalRoot };
      const launcherFailures = [
        Buffer.from([0, 0, 0, 0]),
        Buffer.from([0x01, 0x20, 0, 0]),
        framed({ ...launcherBootstrap, extra: true }),
        framed({ ...launcherBootstrap, protocol: 'wrong' }),
        framedJson(JSON.stringify(launcherBootstrap).replace(/}$/, ',"root":"duplicate"}')),
        Buffer.concat([framed(launcherBootstrap), framed({ protocol: MAINTENANCE_FS_PROTOCOL, operation: 'handshake' })]),
        ...Object.keys(launcherBootstrap).map((omitted) => framed(Object.fromEntries(Object.entries(launcherBootstrap).filter(([key]) => key !== omitted)))),
        ...['relative.json', '\\\\server\\share\\manifest.json', '\\\\?\\C:\\package\\manifest.json', 'C:\\package\\manifest.json:stream']
          .map((invalidManifestPath) => framed({ ...launcherBootstrap, manifestPath: invalidManifestPath })),
      ];
      for (const bytes of launcherFailures) {
        const malformed = await runRawLauncher(launcher, bytes);
        assert.notEqual(malformed.code, 0);
        assert.equal(malformed.stderr.length, 0);
        for (const privateValue of [originalRoot, manifestPath, manifestHash]) assert.equal(Buffer.concat([malformed.stdout, malformed.stderr]).includes(Buffer.from(privateValue)), false);
      }
      const disconnected = await runRawLauncher(launcher, framed(launcherBootstrap));
      assert.ok(disconnected.code === 0 || disconnected.code === 5, 'parent disconnect must use a fixed launcher exit');
      assert.ok(disconnected.durationMs < 3000, `disconnect containment exceeded bound: ${disconnected.durationMs}ms`);
      assert.equal(disconnected.stderr.length, 0);
      for (const privateValue of [originalRoot, manifestPath, manifestHash]) assert.equal(disconnected.stdout.includes(Buffer.from(privateValue)), false);

      await runPackageRejectionCases({ launcher, sourceHelper: helper, root: retainedRoot });
      await runChildFailureCases({ launcher, fixtureDirectory, fixtureSource, root: retainedRoot });
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
  assert.equal(packageJson.files.includes('tools/CodexProSafe.MaintenanceFsLauncher'), true, 'reusable launcher source must be package-owned');
  const clientSource = await fs.readFile(path.resolve('src/windowsMaintenanceFsBoundary.ts'), 'utf8');
  assert.equal(clientSource.includes('spawn(this.executablePath'), false, 'client must not directly launch the external helper');
  assert.equal(clientSource.includes('["--serve-maintenance-fs"]'), false, 'client must never fall back to direct helper mode');
  assert.match(clientSource, /spawn\(this\.trustedLauncherPath, \["--serve"\]/);
  const maintenanceSources = await Promise.all([
    Promise.resolve(clientSource),
    fs.readFile(path.resolve('tools/CodexProSafe.Manager/DiagnosticHelper/MaintenanceProtocol.cs'), 'utf8'),
    fs.readFile(path.resolve('tools/CodexProSafe.Manager/DiagnosticHelper/MaintenanceFilesystemProvider.cs'), 'utf8'),
    fs.readFile(path.resolve('tools/CodexProSafe.MaintenanceFsLauncher/Program.cs'), 'utf8'),
    fs.readFile(path.resolve('tools/CodexProSafe.MaintenanceFsLauncher/NativeChild.cs'), 'utf8'),
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

function parseFirstFrame(bytes) {
  assert.ok(bytes.length >= 5, 'framed response is incomplete');
  const length = bytes.readUInt32LE(0);
  assert.equal(bytes.length, length + 4, 'framed response length mismatch');
  return JSON.parse(bytes.subarray(4).toString('utf8'));
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

function runHelperRequest(helper, bytes) {
  return new Promise((resolve, reject) => {
    const child = spawn(helper, ['--serve-maintenance-fs'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, shell: false });
    const stdout = [];
    const stderr = [];
    child.stdout.on('data', (chunk) => stdout.push(chunk));
    child.stderr.on('data', (chunk) => stderr.push(chunk));
    child.on('error', reject);
    child.on('exit', (code) => resolve({ code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr) }));
    child.stdin.write(bytes);
  });
}

function runRawLauncher(launcher, bytes) {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const child = spawn(launcher, ['--serve'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, shell: false });
    const stdout = [];
    const stderr = [];
    child.stdout.on('data', (chunk) => stdout.push(chunk));
    child.stderr.on('data', (chunk) => stderr.push(chunk));
    child.on('error', reject);
    child.on('exit', (code) => resolve({ code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr), durationMs: Date.now() - started }));
    child.stdin.end(bytes);
  });
}

async function runPackageRejectionCases({ launcher, sourceHelper, root }) {
  async function createPackage() {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-launcher-package-'));
    const helper = path.join(directory, 'CodexProSafe.DiagnosticHelper.exe');
    const manifest = path.join(directory, 'CodexProSafe.DiagnosticHelper.json');
    await fs.copyFile(sourceHelper, helper);
    const sha256 = await sha256File(helper);
    await fs.writeFile(manifest, JSON.stringify({ protocolVersion: 'codexpro-diagnostic-v1', maintenanceFsProtocolVersion: MAINTENANCE_FS_PROTOCOL, executable: 'CodexProSafe.DiagnosticHelper.exe', sha256 }));
    return { directory, helper, manifest, manifestHash: await sha256File(manifest) };
  }
  async function rejectsPackage(value) {
    const boundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: launcher, manifestPath: value.manifest, expectedManifestSha256: value.manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: root });
    await assert.rejects(boundary.ready(), /exited|unavailable/);
    await boundary.close().catch(() => undefined);
  }

  for (const mutation of [
    async (value) => fs.writeFile(value.manifest, '{}'),
    async (value) => fs.writeFile(value.helper, 'replacement'),
    async (value) => fs.link(value.helper, path.join(value.directory, 'helper-hardlink.exe')),
    async (value) => { await fs.unlink(value.manifest); const target = path.join(value.directory, 'manifest-junction-target'); await fs.mkdir(target); await fs.symlink(target, value.manifest, 'junction'); },
    async (value) => { await fs.unlink(value.helper); const target = path.join(value.directory, 'helper-junction-target'); await fs.mkdir(target); await fs.symlink(target, value.helper, 'junction'); },
  ]) {
    const value = await createPackage();
    try { await mutation(value); await rejectsPackage(value); }
    finally { await fs.rm(value.directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 }); }
  }

  const missing = await createPackage();
  try {
    await fs.unlink(missing.helper);
    await rejectsPackage(missing);
  } finally { await fs.rm(missing.directory, { recursive: true, force: true }); }
}

async function runChildFailureCases({ launcher, fixtureDirectory, fixtureSource, root }) {
  async function packageHelper(source) {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-launcher-child-'));
    const helper = path.join(directory, 'CodexProSafe.DiagnosticHelper.exe');
    const manifest = path.join(directory, 'CodexProSafe.DiagnosticHelper.json');
    await fs.copyFile(source, helper);
    const sha256 = await sha256File(helper);
    await fs.writeFile(manifest, JSON.stringify({ protocolVersion: 'codexpro-diagnostic-v1', maintenanceFsProtocolVersion: MAINTENANCE_FS_PROTOCOL, executable: 'CodexProSafe.DiagnosticHelper.exe', sha256 }));
    return { directory, manifest, manifestHash: await sha256File(manifest) };
  }
  async function rejectAndUnlock(value, expected) {
    const boundary = new ManagedWindowsMaintenanceFsBoundary({ trustedLauncherPath: launcher, manifestPath: value.manifest, expectedManifestSha256: value.manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, rootPath: root });
    await assert.rejects(boundary.ready(), expected);
    await fs.rm(value.directory, { recursive: true, force: true, maxRetries: 30, retryDelay: 100 });
  }

  const invalidExecutable = path.join(fixtureDirectory, 'invalid-helper.exe');
  await fs.writeFile(invalidExecutable, 'not a Windows executable');
  await rejectAndUnlock(await packageHelper(invalidExecutable), /exited|unavailable/);

  const hangingHelper = compileClientFixture(fixtureDirectory, fixtureSource, 'hanging-helper', 'HELPER_TIMEOUT');
  const disconnectedPackage = await packageHelper(hangingHelper);
  const disconnected = await runRawLauncher(launcher, framed({ protocol: 'codexpro-maintenance-fs-launcher-v1', operation: 'bootstrap', manifestPath: disconnectedPackage.manifest, expectedManifestSha256: disconnectedPackage.manifestHash, expectedMaintenanceProtocol: MAINTENANCE_FS_PROTOCOL, root }));
  assert.notEqual(disconnected.code, 0);
  assert.equal(disconnected.stderr.length, 0);
  assert.ok(disconnected.durationMs < 3000, `disconnect containment exceeded bound: ${disconnected.durationMs}ms`);
  await fs.rm(disconnectedPackage.directory, { recursive: true, force: true, maxRetries: 30, retryDelay: 100 });

  await rejectAndUnlock(await packageHelper(hangingHelper), /timeout|exited/);
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
