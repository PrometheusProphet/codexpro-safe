import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  STALE_BUILD_ERROR,
  assertBuildFreshness,
  inspectBuildFreshness,
  missingBuildError
} from './build-freshness.mjs';

const fixtureRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro build freshness '));
const oldTime = new Date('2024-01-01T00:00:00.000Z');
const buildTime = new Date('2024-01-02T00:00:00.000Z');
const newTime = new Date('2024-01-03T00:00:00.000Z');

async function writeFixture(name, { source = true, build = true } = {}) {
  const root = path.join(fixtureRoot, name);
  await fs.mkdir(root, { recursive: true });
  if (source) {
    await fs.mkdir(path.join(root, 'src', 'nested'), { recursive: true });
    await fs.writeFile(path.join(root, 'src', 'nested', 'server.ts'), 'export const value = 1;\n');
    await fs.writeFile(path.join(root, 'tsconfig.json'), '{}\n');
    await fs.utimes(path.join(root, 'src', 'nested', 'server.ts'), oldTime, oldTime);
    await fs.utimes(path.join(root, 'tsconfig.json'), oldTime, oldTime);
  }
  if (build) {
    await fs.mkdir(path.join(root, 'dist'), { recursive: true });
    await fs.writeFile(path.join(root, 'dist', 'http.js'), '// built HTTP entry\n');
    await fs.writeFile(path.join(root, 'dist', 'server.js'), '// built server\n');
    await fs.utimes(path.join(root, 'dist', 'http.js'), buildTime, buildTime);
    await fs.utimes(path.join(root, 'dist', 'server.js'), buildTime, buildTime);
  }
  return root;
}

try {
  const freshRoot = await writeFixture('fresh source checkout');
  assert.equal(inspectBuildFreshness({ projectRoot: freshRoot }).status, 'fresh');

  const staleRoot = await writeFixture('stale source checkout');
  await fs.utimes(path.join(staleRoot, 'src', 'nested', 'server.ts'), newTime, newTime);
  assert.throws(
    () => assertBuildFreshness({ projectRoot: staleRoot }),
    (error) => error instanceof Error && error.message === STALE_BUILD_ERROR
  );

  const packagedRoot = await writeFixture('packaged installation', { source: false });
  assert.equal(inspectBuildFreshness({ projectRoot: packagedRoot }).status, 'packaged');

  const missingRoot = await writeFixture('missing build artifact', { build: false });
  const missingHttpPath = path.join(missingRoot, 'dist', 'http.js');
  assert.throws(
    () => assertBuildFreshness({ projectRoot: missingRoot }),
    (error) => error instanceof Error && error.message === missingBuildError(missingHttpPath)
  );
} finally {
  await fs.rm(fixtureRoot, { recursive: true, force: true });
}

console.log('✓ build freshness test passed');
