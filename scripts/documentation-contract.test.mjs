import fs from 'node:fs/promises';

function fail(message) { throw new Error(message); }
function requirePattern(label, text, pattern, message) { if (!pattern.test(text)) fail(`${label}: ${message}`); }
function rejectPattern(label, text, pattern, message) { if (pattern.test(text)) fail(`${label}: ${message}`); }

const activeFiles = ['README.md', 'README_ZH.md', 'FAQ.md', 'FAQ_ZH.md', 'PUBLIC_LAUNCH_CHECKLIST.md'];
const ownerFiles = ['SECURITY.md', 'DOMAIN_SETUP.md'];
const entries = await Promise.all([...activeFiles, ...ownerFiles].map(async (name) => [name, await fs.readFile(name, 'utf8')]));
const docs = Object.fromEntries(entries);
const packageJson = JSON.parse(await fs.readFile('package.json', 'utf8'));
const launcher = await fs.readFile('scripts/codexpro.mjs', 'utf8');

if (packageJson.name !== 'codexpro-safe') fail('package.json must keep codexpro-safe as the npm package identity');
if (packageJson.bin?.['codexpro-safe'] !== 'scripts/codexpro.mjs') fail('canonical codexpro-safe CLI bin is missing');
if (packageJson.bin?.codexpro !== packageJson.bin?.['codexpro-safe']) fail('supported codexpro CLI alias is missing or points elsewhere');
for (const invariant of ["const DEFAULT_TUNNEL = 'none'", "const DEFAULT_MODE = 'handoff'", "const DEFAULT_BASH = 'off'", "const DEFAULT_WRITE = 'handoff'"]) if (!launcher.includes(invariant)) fail(`launcher default changed without documentation-contract review: ${invariant}`);
for (const invariant of ['--save-config', '--profile', 'const shouldLoadSetupProfile = defaults.useProfile === true && !defaults.noProfile;']) if (!launcher.includes(invariant)) fail(`launcher profile contract changed without documentation-contract review: ${invariant}`);

for (const name of activeFiles) {
  requirePattern(name, docs[name], /codexpro-safe/iu, 'current user-facing document must identify the Safe package');
  rejectPattern(name, docs[name], /npm\s+(?:install|i)(?:\s+--global|\s+-g)?\s+codexpro(?:@(?:latest|[0-9])|\s|$)/imu, 'must not instruct Safe users to install the separate upstream codexpro package');
  rejectPattern(name, docs[name], /(?:mode (?:is|defaults to) agent|agent mode by default|bash (?:is|defaults to) safe|bash safe by default|write(?: mode)? (?:is|defaults to) workspace|workspace writes? by default|tunnel (?:is|defaults to) cloudflare|cloudflare tunnel by default)/iu, 'must not contradict the conservative launcher defaults');
}

for (const name of ['README.md', 'FAQ.md']) {
  requirePattern(name, docs[name], /handoff/iu, 'must describe the default handoff posture');
  requirePattern(name, docs[name], /bash (?:is )?off|bash off/iu, 'must describe bash as off by default');
  requirePattern(name, docs[name], /source (?:writes?|`write`\/`edit`)[^.\n]*(?:blocked|not advertised|explicitly enable)|generic source[^.\n]*(?:not|unless)/iu, 'must keep generic source writes outside the ordinary default posture');
}
for (const name of ['README_ZH.md', 'FAQ_ZH.md']) {
  requirePattern(name, docs[name], /handoff/iu, 'translated onboarding must preserve the handoff default');
  requirePattern(name, docs[name], /bash[^。\n]*(?:关闭|显式启用)/iu, 'translated onboarding must preserve bash-off behavior');
  requirePattern(name, docs[name], /(?:通用源码[^。\n]*必须显式|不会提供通用源码|只有[^。\n]*显式启用[^。\n]*(?:workspace write|write))/iu, 'translated onboarding must preserve explicit source-write behavior');
}
requirePattern('README.md', docs['README.md'], /no public tunnel|Public exposure is always explicit/iu, 'public tunnels must remain explicit');
requirePattern('README_ZH.md', docs['README_ZH.md'], /公网 tunnel[^。\n]*显式|必须显式使用 `--tunnel cloudflare`/iu, 'translated onboarding must keep public tunnels explicit');
requirePattern('PUBLIC_LAUNCH_CHECKLIST.md', docs['PUBLIC_LAUNCH_CHECKLIST.md'], /default handoff mode/iu, 'launch verification must exercise the Safe handoff default');
requirePattern('PUBLIC_LAUNCH_CHECKLIST.md', docs['PUBLIC_LAUNCH_CHECKLIST.md'], /bash off by default/iu, 'launch verification must preserve bash-off');

for (const name of ['README.md', 'README_ZH.md', 'FAQ.md', 'FAQ_ZH.md']) {
  requirePattern(name, docs[name], /(?:compatibility alias|supported CLI alias|受支持的 CLI 别名|兼容别名)/iu, 'must label the codexpro compatibility alias');
  requirePattern(name, docs[name], /\bcodexpro(?:\s|`)/iu, 'must preserve the supported codexpro command alias');
  requirePattern(name, docs[name], /--save-config/iu, 'must name the opt-in profile save flag');
  requirePattern(name, docs[name], /--profile/iu, 'must name the explicit profile reuse flag');
}
requirePattern('README.md', docs['README.md'], /does not load saved profiles unless you pass --profile|does not load saved profiles unless you pass `--profile`/iu, 'ordinary startup must not imply silent profile reuse');
requirePattern('README_ZH.md', docs['README_ZH.md'], /不会静默保存|不会静默.*profile/iu, 'translated onboarding must reject silent profile persistence');
requirePattern('FAQ.md', docs['FAQ.md'], /Plain `setup` or\s*`start` does not silently/iu, 'FAQ must reject silent profile save/load');
requirePattern('FAQ_ZH.md', docs['FAQ_ZH.md'], /不会静默保存或加载 profile/iu, 'translated FAQ must reject silent profile save/load');
for (const flag of ['--save-config', '--profile']) requirePattern('DOMAIN_SETUP.md', docs['DOMAIN_SETUP.md'], new RegExp(flag, 'u'), `detailed profile owner must name ${flag}`);

for (const name of ['README.md', 'README_ZH.md', 'FAQ.md', 'FAQ_ZH.md', 'PUBLIC_LAUNCH_CHECKLIST.md']) {
  requirePattern(name, docs[name], /SECURITY\.md/iu, 'must route detailed safety guidance to SECURITY.md');
  requirePattern(name, docs[name], /DOMAIN_SETUP\.md/iu, 'must route detailed domain/profile setup to DOMAIN_SETUP.md');
}
requirePattern('SECURITY.md', docs['SECURITY.md'], /normative owner/iu, 'SECURITY.md must remain the detailed normative security owner');
requirePattern('DOMAIN_SETUP.md', docs['DOMAIN_SETUP.md'], /owns the detailed public-tunnel/iu, 'DOMAIN_SETUP.md must remain the detailed tunnel/domain/profile owner');
if (activeFiles.includes('CHANGELOG.md')) fail('historical CHANGELOG.md must remain outside current-document package/default enforcement');

console.log('✓ public documentation contract passed');
