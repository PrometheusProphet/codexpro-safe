import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { ask, labelValue, printBox, statusLine } from './cli-ui.mjs';

function expandHome(input) {
  if (!input || input === '~') return os.homedir();
  if (input.startsWith('~/')) return path.join(os.homedir(), input.slice(2));
  return input;
}

export function codexProHome() {
  const customHome = process.env.CODEXPRO_HOME;
  return customHome ? path.resolve(expandHome(customHome)) : path.join(os.homedir(), '.codexpro');
}

function profileDir() {
  return path.join(codexProHome(), 'profiles');
}

function profileIdForRoot(root) {
  return createHash('sha256').update(root).digest('hex').slice(0, 24);
}

function profilePathForRoot(root) {
  return path.join(profileDir(), `${profileIdForRoot(root)}.json`);
}

function readJsonFile(filePath) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch (error) {
    if (error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT') return {};
    throw error;
  }
}

export function loadWorkspaceProfile(root) {
  const profilePath = profilePathForRoot(root);
  if (!fs.existsSync(profilePath)) return {};
  const profile = readJsonFile(profilePath);
  if (!profile || typeof profile !== 'object' || Array.isArray(profile)) return {};
  if (profile.root && profile.root !== root) return {};
  return { ...profile, profilePath };
}

export function listWorkspaceProfiles() {
  const dir = profileDir();
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir)
    .filter((name) => name.endsWith('.json'))
    .map((name) => {
      const profilePath = path.join(dir, name);
      const profile = readJsonFile(profilePath);
      if (!profile || typeof profile !== 'object' || Array.isArray(profile) || !profile.root) return null;
      return { ...profile, profilePath };
    })
    .filter(Boolean)
    .sort((a, b) => String(b.updatedAt || '').localeCompare(String(a.updatedAt || '')));
}

export function deleteWorkspaceProfile(root) {
  const filePath = profilePathForRoot(root);
  if (!fs.existsSync(filePath)) return false;
  fs.rmSync(filePath, { force: true });
  return true;
}

export function saveWorkspaceProfile(root, profile) {
  const dir = profileDir();
  const filePath = profilePathForRoot(root);
  fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  const payload = { version: 1, root, updatedAt: new Date().toISOString(), ...profile };
  fs.writeFileSync(filePath, `${JSON.stringify(payload, null, 2)}\n`, { mode: 0o600 });
  try {
    fs.chmodSync(filePath, 0o600);
  } catch {}
  return filePath;
}

function sanitizedProfile(profile) {
  if (!profile || !Object.keys(profile).length) return {};
  const { token, cloudflareToken, ...rest } = profile;
  return {
    ...rest,
    ...(token ? { token: '<saved>' } : {}),
    ...(cloudflareToken ? { cloudflareToken: '<saved>' } : {}),
  };
}

export function reusableProfilePayload(profile, overrides = {}) {
  const { version, root, updatedAt, profilePath, ...rest } = profile || {};
  return { ...rest, ...overrides };
}

export function optionValue(args, profile, field, envNames = [], fallback = undefined) {
  if (args[field] !== undefined) return args[field];
  for (const envName of envNames) {
    if (process.env[envName] !== undefined && process.env[envName] !== '') return process.env[envName];
  }
  if (profile?.[field] !== undefined && profile[field] !== '') return profile[field];
  return fallback;
}

export function stableToken(existing = '') {
  return existing || randomBytes(24).toString('hex');
}

export function profileSummary(profile) {
  if (!profile?.tunnel) return '';
  if (profile.tunnel === 'ngrok' && profile.hostname) return `Saved ngrok URL: ${profile.hostname}`;
  if (profile.tunnel === 'cloudflare-named' && profile.hostname) return `Saved Cloudflare URL: ${profile.hostname}`;
  if (profile.tunnel === 'cloudflare') return 'Saved Cloudflare quick-tunnel setup';
  if (profile.tunnel === 'none') return 'Saved local-only setup';
  return '';
}

function profileOneLine(profile, index = 0, defaultTunnel = 'none') {
  const prefix = index ? `${index}. ` : '';
  const tunnel = profile.tunnel ?? defaultTunnel;
  const host = profile.hostname ? ` -> ${profile.hostname}` : '';
  const port = profile.port ? ` :${profile.port}` : '';
  return `${prefix}${profile.root}  ${tunnel}${host}${port}`;
}

export function printSavedProfileHint(profile) {
  const summary = profileSummary(profile);
  if (!summary) return;
  printBox('Saved setup found', [
    summary,
    'Use this saved setup for a launch with: codexpro start --profile',
    'Use codexpro setup --profile only when you intentionally want saved values as setup defaults.',
  ]);
}

export function printProfile(root, profile, defaultTunnel = 'none') {
  if (!profile.profilePath) {
    printBox('CodexPro settings', [
      labelValue('Workspace', root),
      'No saved settings for this workspace.',
      'Run codexpro settings set or codexpro setup to save a tunnel preference.',
    ]);
    return;
  }
  const safe = sanitizedProfile(profile);
  printBox('CodexPro settings', [
    labelValue('Workspace', root),
    labelValue('Profile', profile.profilePath),
    labelValue('Tunnel', safe.tunnel ?? defaultTunnel),
    ...(safe.hostname ? [labelValue('Hostname', safe.hostname)] : []),
    ...(safe.port ? [labelValue('Port', safe.port)] : []),
    ...(safe.mode ? [labelValue('Mode', safe.mode)] : []),
    ...(safe.toolMode ? [labelValue('Tool mode', safe.toolMode)] : []),
    ...(safe.toolCardMode ? [labelValue('Tool cards', safe.toolCardMode)] : []),
    ...(safe.widgetDomain ? [labelValue('Widget domain', safe.widgetDomain)] : []),
    ...(safe.token ? [labelValue('Token', safe.token)] : []),
  ]);
}

export function printProfileList(profiles = listWorkspaceProfiles(), defaultTunnel = 'none') {
  if (!profiles.length) {
    printBox('CodexPro saved setups', [
      'No saved workspace settings found.',
      'Run codexpro setup or codexpro settings set to create one.',
    ]);
    return;
  }
  printBox('CodexPro saved setups', profiles.slice(0, 50).map((profile, index) => profileOneLine(profile, index + 1, defaultTunnel)));
}

export async function chooseReusableProfile(rl, currentRoot, profiles = listWorkspaceProfiles(), defaultTunnel = 'none') {
  const reusable = profiles.filter((item) => item.root !== currentRoot);
  if (!reusable.length) return null;
  printProfileList(reusable, defaultTunnel);
  const answer = await ask(rl, 'Use saved setup number?', reusable.length === 1 ? '1' : '');
  const selectedIndex = Number(answer.trim());
  if (!Number.isInteger(selectedIndex) || selectedIndex < 1 || selectedIndex > reusable.length) throw new Error('Invalid saved setup number.');
  return reusable[selectedIndex - 1];
}

export function saveSettingsFromArgs(root, args, profile, defaults = {}) {
  const defaultTunnel = defaults.tunnel ?? 'none';
  const defaultMode = defaults.mode ?? 'handoff';
  const tunnel = optionValue(args, profile, 'tunnel', ['CODEXPRO_TUNNEL'], profile.tunnel ?? defaultTunnel);
  if (!['none', 'cloudflare', 'cloudflare-named', 'ngrok'].includes(tunnel)) throw new Error('--tunnel must be none, cloudflare, cloudflare-named, or ngrok');
  const hostname = args.hostname ?? args.url ?? profile.hostname ?? '';
  if ((tunnel === 'ngrok' || tunnel === 'cloudflare-named') && !hostname) throw new Error('--hostname is required for ngrok and cloudflare-named settings.');
  const mode = optionValue(args, profile, 'mode', ['CODEXPRO_MODE'], profile.mode ?? defaultMode);
  const toolMode = optionValue(args, profile, 'toolMode', ['CODEXPRO_TOOL_MODE'], profile.toolMode ?? '');
  const toolCardMode = optionValue(args, profile, 'toolCardMode', ['CODEXPRO_TOOL_CARD_MODE'], profile.toolCardMode ?? '');
  if (toolCardMode && !['off', 'compact'].includes(toolCardMode)) throw new Error('--tool-card-mode must be off or compact');
  const widgetDomain = optionValue(args, profile, 'widgetDomain', ['CODEXPRO_WIDGET_DOMAIN'], profile.widgetDomain ?? '');
  const port = String(optionValue(args, profile, 'port', ['CODEXPRO_PORT'], profile.port ?? '8787'));
  const existingToken = optionValue(args, profile, 'token', ['CODEXPRO_HTTP_TOKEN', 'CODEBASE_BRIDGE_HTTP_TOKEN'], profile.token ?? '');
  const token = tunnel === 'none' ? existingToken : stableToken(existingToken);
  const savedPath = saveWorkspaceProfile(root, {
    port,
    mode,
    tunnel,
    ...(hostname ? { hostname } : {}),
    ...(args.tunnelName ?? profile.tunnelName ? { tunnelName: args.tunnelName ?? profile.tunnelName } : {}),
    ...(args.ngrokConfig ?? profile.ngrokConfig ? { ngrokConfig: args.ngrokConfig ?? profile.ngrokConfig } : {}),
    ...(args.cloudflareConfig ?? profile.cloudflareConfig ? { cloudflareConfig: args.cloudflareConfig ?? profile.cloudflareConfig } : {}),
    ...(args.cloudflareTokenFile ?? profile.cloudflareTokenFile ? { cloudflareTokenFile: args.cloudflareTokenFile ?? profile.cloudflareTokenFile } : {}),
    ...(token ? { token } : {}),
    ...(args.bash ?? profile.bash ? { bash: args.bash ?? profile.bash } : {}),
    ...(args.write ?? profile.write ? { write: args.write ?? profile.write } : {}),
    ...(toolMode ? { toolMode } : {}),
    ...(toolCardMode ? { toolCardMode } : {}),
    ...(widgetDomain ? { widgetDomain } : {}),
    ...(args.noInstallCloudflared ?? profile.noInstallCloudflared ? { noInstallCloudflared: true } : {}),
  });
  statusLine('ok', `Saved workspace settings: ${savedPath}`);
  printProfile(root, loadWorkspaceProfile(root), defaultTunnel);
}
