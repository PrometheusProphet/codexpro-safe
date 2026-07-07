import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

function fail(message) {
  throw new Error(message);
}

async function readText(filePath) {
  return fs.readFile(filePath, 'utf8');
}

function assertNoPattern(label, text, pattern, message) {
  if (pattern.test(text)) fail(`${label}: ${message}`);
}

function withCleanCodexproEnv(fn) {
  const saved = {};
  for (const key of Object.keys(process.env)) {
    if (key.startsWith('CODEXPRO_') || key.startsWith('CODEBASE_BRIDGE_')) {
      saved[key] = process.env[key];
      delete process.env[key];
    }
  }
  return Promise.resolve()
    .then(fn)
    .finally(() => {
      for (const key of Object.keys(process.env)) {
        if (key.startsWith('CODEXPRO_') || key.startsWith('CODEBASE_BRIDGE_')) delete process.env[key];
      }
      Object.assign(process.env, saved);
    });
}

await withCleanCodexproEnv(async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'codexpro-safe-defaults-'));
  const { loadConfig } = await import('../dist/config.js');
  const config = loadConfig(['--root', root]);
  if (config.bashMode !== 'off') fail(`default bashMode should be off, got ${config.bashMode}`);
  if (config.writeMode !== 'handoff') fail(`default writeMode should be handoff, got ${config.writeMode}`);
  if (config.toolMode !== 'standard') fail(`default toolMode should be standard, got ${config.toolMode}`);
  if (config.host !== '127.0.0.1') fail(`default host should be 127.0.0.1, got ${config.host}`);
  if (config.allowQueryToken !== false) fail('CODEXPRO_ALLOW_QUERY_TOKEN unset should default false');
  if (config.allowSymlinks !== false) fail('CODEXPRO_ALLOW_SYMLINKS unset should default false');
  if (!Array.isArray(config.corsOrigins) || config.corsOrigins.length !== 0) fail(`default corsOrigins should be empty, got ${JSON.stringify(config.corsOrigins)}`);
});

const { redactSensitiveText, redactSensitiveTextWithCount, hasSecretValue } = await import('../dist/redact.js');
const fakePemPrivateKey = ['-----BEGIN ', 'PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END ', 'PRIVATE KEY-----'].join('');
const sensitiveSamples = [
  'Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payloadpart.signaturepart',
  'const jwt = "aaaaaaaaaaaa.bbbbbbbbbbbb.cccccccccccc";',
  fakePemPrivateKey,
  'https://user:password@example.test/path',
  'https://example.test/callback?access_token=abcdefghijklmnopqrstuvwxyz123456&ok=1',
  'const blob = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";',
  'SERVICE_TOKEN="abcdefghijklmnopqrstuvwxyz1234567890"'
];
for (const sample of sensitiveSamples) {
  const redacted = redactSensitiveTextWithCount(sample);
  if (redacted.count < 1 || !redacted.text.includes('[REDACTED_SECRET]')) {
    fail(`redaction missed sensitive sample: ${sample}`);
  }
  if (!hasSecretValue(sample)) fail(`hasSecretValue missed sensitive sample: ${sample}`);
}
const placeholderSamples = [
  'OPENAI_API_KEY=process.env.OPENAI_API_KEY',
  'TOKEN=import.meta.env.VITE_TOKEN',
  'password = os.environ["PASSWORD"]',
  'secret = getenv("SECRET")',
  'api_key=replace-me',
  'Authorization: Bearer <token>',
  'OPENAI_API_KEY=[REDACTED_SECRET]'
];
for (const sample of placeholderSamples) {
  const redacted = redactSensitiveText(sample);
  if (redacted !== sample || hasSecretValue(sample)) {
    fail(`redaction should preserve placeholder sample: ${sample} -> ${redacted}`);
  }
}

const packageJson = JSON.parse(await readText('package.json'));
if (packageJson.name !== 'codexpro-safe') fail(`package name should be codexpro-safe, got ${packageJson.name}`);
if (packageJson.bin?.['codexpro-safe'] !== 'scripts/codexpro.mjs') fail('codexpro-safe primary bin missing');
if (packageJson.bin?.codexpro !== 'scripts/codexpro.mjs') fail('codexpro compatibility bin missing');

const cli = await readText('scripts/codexpro.mjs');
for (const expected of [
  "const DEFAULT_TUNNEL = 'none'",
  "const DEFAULT_MODE = 'handoff'",
  "const DEFAULT_BASH = 'off'",
  "const DEFAULT_WRITE = 'handoff'",
  "const shouldLoadSetupProfile = defaults.useProfile === true && !defaults.noProfile;",
  "const profile = shouldLoadSetupProfile ? loadWorkspaceProfile(root) : {};",
  "if (!token || !allowQueryToken) return endpoint;",
  "if (!args.installCloudflared) return '';"
]) {
  if (!cli.includes(expected)) fail(`CLI missing safety invariant: ${expected}`);
}
assertNoPattern('CLI', cli, /Default:\s*agent/i, 'help still claims agent is default');
assertNoPattern('CLI', cli, /Default:\s*safe/i, 'help still claims bash safe is default');
assertNoPattern('CLI', cli, /Default:\s*workspace/i, 'help still claims workspace writes are default');
assertNoPattern('CLI', cli, /Default:\s*cloudflare/i, 'help still claims cloudflare tunnel is default');
assertNoPattern('CLI', cli, /defaultTunnel:\s*'cloudflare'/i, 'first-run default tunnel is still cloudflare');
assertNoPattern('CLI', cli, /const profile = defaults\.noProfile \? \{\} : loadWorkspaceProfile\(root\);/, 'setup still loads saved profiles by default');
assertNoPattern('CLI', cli, /future launches only need:\s*codexpro start/i, 'saved profile hint still says plain start uses saved profiles');
assertNoPattern('CLI', cli, /auto-install/i, 'launcher still advertises automatic cloudflared install');

const docsFiles = ['README.md', 'SECURITY.md', 'config.example.env', 'FAQ.md', 'DOMAIN_SETUP.md'];
const docsText = (await Promise.all(docsFiles.map(readText))).join('\n');
const docsWithoutCompatibilityNotes = docsText.replace(/Temporary compatibility only:[^\n]*(?:\n|$)/gi, '');
assertNoPattern('docs', docsText, /copied Server URL already includes/i, 'docs still claim copied URL contains the token by default');
assertNoPattern('docs', docsText, /private CodexPro token[^.\n]*copied URL/i, 'docs still tie private token to copied URL by default');
assertNoPattern('docs', docsWithoutCompatibilityNotes, /Server URL:\s*https?:\/\/[^\s\n]*\?codexpro_token=/i, 'docs still show query-token URLs as normal setup');
assertNoPattern('docs', docsWithoutCompatibilityNotes, /Server URL:\s*https?:\/\/(?!(?:127\.0\.0\.1|localhost)(?::|\/))[^\n]+\nAuthentication:\s*(?:No Authentication\s*\/\s*None|None\s*\/\s*No Authentication)/i, 'docs still show no-auth as normal public Server URL setup');
assertNoPattern('docs', docsText, /default[^.\n]*bash[^.\n]*safe/i, 'docs still claim bash safe is the default');
assertNoPattern('docs', docsText, /default[^.\n]*workspace[- ]write|workspace[- ]write[^.\n]*default/i, 'docs still claim workspace writes are the default');
assertNoPattern('docs', docsText, /default[^.\n]*cloudflare/i, 'docs still claim Cloudflare is the default tunnel');
assertNoPattern('docs', docsText, /auto-installs|auto-install/i, 'docs still claim cloudflared auto-install behavior');

console.log('✓ safe defaults smoke test passed');
