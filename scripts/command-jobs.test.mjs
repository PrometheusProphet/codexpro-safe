import assert from 'node:assert/strict';
import test from 'node:test';

import {
  COMMAND_STATUS_WAIT_MAX_MS,
  COMMAND_SYNC_BUDGET_MS,
  CommandJobRegistry,
  startCommandJobWithBudget,
} from '../dist/commandJobs.js';

const completedResult = {
  command: 'example',
  runner: process.platform === 'win32' ? 'powershell' : 'bash',
  cwd: '.',
  exitCode: 0,
  signal: null,
  durationMs: 125_000,
  stdout: 'done',
  stderr: '',
  truncated: false,
};

test('long command jobs remain running across a bounded status wait and retain the final result', async () => {
  assert.equal(COMMAND_SYNC_BUDGET_MS, 90_000);
  assert.equal(COMMAND_STATUS_WAIT_MAX_MS, 30_000);

  const registry = new CommandJobRegistry();
  let resolveCommand;
  const job = await startCommandJobWithBudget(
    registry,
    'ws-test',
    'example',
    () => new Promise((resolve) => {
      resolveCommand = resolve;
    }),
    5,
  );

  assert.equal(job.state, 'running');

  resolveCommand(completedResult);
  await job.settled;
  assert.equal(job.state, 'completed');
  assert.deepEqual(job.result, completedResult);
  assert.equal(registry.get(job.id), job);

  registry.delete(job.id);
  assert.equal(registry.get(job.id), undefined);
});

test('command job failures settle without creating unhandled rejections', async () => {
  const registry = new CommandJobRegistry();
  const expected = new Error('synthetic failure');
  const job = registry.start('ws-test', 'failure', async () => {
    throw expected;
  });

  await job.settled;
  assert.equal(job.state, 'failed');
  assert.equal(job.error, expected);
});
