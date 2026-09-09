import assert from 'node:assert/strict';
import fs from 'node:fs';

const sourcePath = 'tools/CodexProSafe.Manager.Mac/Sources/CodexProSafeManager/CodexProSafeManagerApp.swift';
const source = fs.readFileSync(sourcePath, 'utf8');
const requiredIdentifiers = [
  'service-status', 'start-all', 'restart-all', 'stop-all', 'takeover-existing',
  'takeover-confirm', 'takeover-cancel', 'settings-link', 'quit-manager',
  'node-path', 'connector-port', 'access-profile', 'tunnel-mode',
  'tunnel-profile',
  'tunnel-health-port', 'organization-id', 'openai-runtime-key',
  'save-openai-runtime-key', 'connector-bearer-token', 'restart-on-failure',
  'auto-start-services', 'launch-at-login', 'save-settings',
  'save-connector-token', 'diagnostic-helper-status', 'settings-status'
];

for (const identifier of requiredIdentifiers) {
  const literal = `accessibilityIdentifier("${identifier}")`;
  assert.equal(source.split(literal).length - 1, 1, `Expected one stable accessibility identifier: ${identifier}`);
}
const pickerBases = ['repository', 'workspace-root', 'allowed-root', 'tunnel-client'];
for (const identifier of pickerBases) {
  assert.equal(source.split(`identifier: "${identifier}"`).length - 1, 1,
    `Expected one stable picker identifier base: ${identifier}`);
}
assert.equal(source.split('accessibilityIdentifier("\\(identifier)-field")').length - 1, 2);
assert.equal(source.split('accessibilityIdentifier("\\(identifier)-choose")').length - 1, 2);
assert.match(source, /Planning is the safest default/);
assert.match(source, /Local only is the default/);
assert.match(source, /stored in the current user's Keychain/);
assert.match(source, /Verifies an exact external connector/);
console.log(`✓ macOS Manager accessibility contract passed (${requiredIdentifiers.length + pickerBases.length * 2} stable identifiers)`);
