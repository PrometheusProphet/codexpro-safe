import fs from 'node:fs';
import path from 'node:path';

export const STALE_BUILD_ERROR = 'Build artifacts are stale; run npm run build.';

const TYPESCRIPT_SOURCE = /\.(?:[cm]?ts|tsx)$/i;

function newestTypeScriptMtime(directory) {
  let newest = 0;
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      newest = Math.max(newest, newestTypeScriptMtime(entryPath));
    } else if (entry.isFile() && TYPESCRIPT_SOURCE.test(entry.name)) {
      newest = Math.max(newest, fs.statSync(entryPath).mtimeMs);
    }
  }
  return newest;
}

export function inspectBuildFreshness({
  projectRoot,
  entryArtifact = path.join('dist', 'http.js'),
  requiredArtifacts = [entryArtifact, path.join('dist', 'server.js')]
}) {
  const resolvedRoot = path.resolve(projectRoot);
  const artifactPaths = requiredArtifacts.map((artifact) => path.resolve(resolvedRoot, artifact));
  const missingArtifacts = artifactPaths.filter((artifact) => !fs.existsSync(artifact));
  const entryArtifactPath = path.resolve(resolvedRoot, entryArtifact);

  if (missingArtifacts.length > 0) {
    return { status: 'missing', entryArtifactPath, missingArtifacts };
  }

  const sourceDir = path.join(resolvedRoot, 'src');
  if (!fs.existsSync(sourceDir)) {
    return { status: 'packaged', entryArtifactPath, missingArtifacts: [] };
  }

  let newestInputMtime = newestTypeScriptMtime(sourceDir);
  const tsconfigPath = path.join(resolvedRoot, 'tsconfig.json');
  if (fs.existsSync(tsconfigPath)) {
    newestInputMtime = Math.max(newestInputMtime, fs.statSync(tsconfigPath).mtimeMs);
  }
  const artifactMtime = fs.statSync(entryArtifactPath).mtimeMs;

  return {
    status: newestInputMtime > artifactMtime ? 'stale' : 'fresh',
    entryArtifactPath,
    missingArtifacts: [],
    newestInputMtime,
    artifactMtime
  };
}

export function missingBuildError(artifactPath) {
  return `Missing ${artifactPath}. Run npm ci --ignore-scripts && npm run build first.`;
}

export function assertBuildFreshness(options) {
  const result = inspectBuildFreshness(options);
  if (result.status === 'missing') throw new Error(missingBuildError(result.missingArtifacts[0]));
  if (result.status === 'stale') throw new Error(STALE_BUILD_ERROR);
  return result;
}
