import assert from 'node:assert/strict';
import test from 'node:test';
import { annotationSummary, toolExposureForMode } from '../dist/toolPolicy.js';

function config(overrides = {}) {
  return {
    toolMode: 'standard',
    bashMode: 'off',
    writeMode: 'handoff',
    codexDiagnosticReadMode: 'off',
    ...overrides,
  };
}

test('minimal mode keeps the bounded inspection surface and explains hidden advanced tools', () => {
  const exposure = toolExposureForMode(config({ toolMode: 'minimal' }));
  assert.deepEqual(exposure.effectiveTools, [
    'server_config',
    'codexpro_self_test',
    'open_current_workspace',
    'open_workspace',
    'source_outline',
    'read_source_lines',
    'show_changes',
  ]);
  assert.deepEqual(exposure.hiddenTools.map((tool) => tool.name), ['read', 'bash', 'write', 'edit']);
});

test('standard mode adds only advanced tools enabled by their safety modes', () => {
  const exposure = toolExposureForMode(config({ bashMode: 'safe', writeMode: 'workspace' }));
  assert.equal(exposure.effectiveTools.includes('bash'), true);
  assert.equal(exposure.effectiveTools.includes('write'), true);
  assert.equal(exposure.effectiveTools.includes('edit'), true);
  assert.equal(exposure.effectiveTools.includes('read'), false);
  assert.deepEqual(exposure.hiddenTools.map((tool) => tool.name), ['read']);
});

test('diagnostic read mode adds only the fixed diagnostic tools', () => {
  const exposure = toolExposureForMode(config({ toolMode: 'minimal', codexDiagnosticReadMode: 'read' }));
  assert.deepEqual(exposure.effectiveTools.slice(-3), [
    'codex_diagnostic_inventory',
    'codex_diagnostic_config_summary',
    'codex_diagnostic_sqlite_metadata',
  ]);
});

test('full mode preserves the complete compatibility catalog', () => {
  const exposure = toolExposureForMode(config({ toolMode: 'full' }));
  for (const name of ['read', 'write', 'edit', 'bash', 'git_diff', 'handoff_to_codex']) {
    assert.equal(exposure.effectiveTools.includes(name), true, `full mode omitted ${name}`);
  }
  assert.deepEqual(exposure.hiddenTools, []);
});

test('annotation summaries preserve read, session, local-write, handoff, and open-world classifications', () => {
  const summary = annotationSummary(['server_config', 'open_workspace', 'write', 'bash', 'handoff_to_agent']);
  assert.deepEqual(summary.counts, {
    read_only: 2,
    local_write: 2,
    handoff_write: 1,
    open_world: 1,
    destructive: 2,
  });
  assert.equal(summary.by_tool.open_workspace.idempotentHint, false);
  assert.equal(summary.by_tool.write.destructiveHint, true);
  assert.equal(summary.by_tool.bash.openWorldHint, true);
  assert.equal(summary.by_tool.handoff_to_agent.destructiveHint, false);
});
