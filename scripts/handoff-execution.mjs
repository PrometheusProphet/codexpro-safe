import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { createInterface } from 'node:readline/promises';
import { ask, labelValue, printBox, statusLine } from './cli-ui.mjs';

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function isSubpath(child, parent) {
  const relative = path.relative(parent, child);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function contextDirFromArgs(args) {
  return args.contextDir ?? process.env.CODEXPRO_CONTEXT_DIR ?? '.ai-bridge';
}

function resolveWorkspaceFile(root, relativePath) {
  const absPath = path.resolve(root, relativePath);
  if (!isSubpath(absPath, root)) throw new Error(`Path escapes workspace root: ${relativePath}`);
  return absPath;
}

function readTextFileBounded(filePath, maxBytes) {
  const stat = fs.statSync(filePath);
  if (!stat.isFile()) throw new Error(`Not a file: ${filePath}`);
  if (stat.size > maxBytes) throw new Error(`File is too large (${stat.size} bytes). Limit: ${maxBytes} bytes.`);
  const sample = fs.readFileSync(filePath, { encoding: null });
  if (sample.includes(0)) throw new Error(`Refusing to read binary file: ${filePath}`);
  return sample.toString('utf8');
}

function numberOption(value, fallback, min, max) {
  const parsed = Number(value ?? fallback);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.max(min, Math.min(max, Math.floor(parsed)));
}

function shellCommandPreview(parts) {
  return parts.map((part) => {
    const text = String(part);
    if (/^[A-Za-z0-9_./:@=+-]+$/.test(text)) return text;
    return `'${text.replace(/'/g, "'\\''")}'`;
  }).join(' ');
}

function redactForLog(value) {
  return String(value)
    .replace(/\bsk-[A-Za-z0-9_-]{10,}\b/g, '[REDACTED_SECRET]')
    .replace(/\b[A-Za-z0-9_]*(?:API[_-]?KEY|TOKEN|SECRET|PASSWORD|PRIVATE[_-]?KEY)[A-Za-z0-9_]*\s*=\s*(?:"[^"\r\n]{12,}"|'[^'\r\n]{12,}'|`[^`\r\n]{12,}`|[A-Za-z0-9_./+=-]{20,})/gi, (match) => {
      const index = match.indexOf('=');
      return index < 0 ? '[REDACTED_SECRET]' : `${match.slice(0, index).trimEnd()}= [REDACTED_SECRET]`;
    });
}

function trimBytes(value, maxBytes) {
  const redacted = redactForLog(value);
  const buffer = Buffer.from(redacted, 'utf8');
  if (buffer.byteLength <= maxBytes) return { text: redacted, truncated: false };
  return {
    text: `${buffer.subarray(0, maxBytes).toString('utf8')}\n...[output truncated to ${maxBytes} bytes]`,
    truncated: true,
  };
}

function splitCommandTemplate(input) {
  const tokens = [];
  let current = '';
  let quote = '';
  const text = String(input);
  for (let i = 0; i < text.length; i += 1) {
    const char = text[i];
    if (char === '\\') {
      const next = text[i + 1];
      if (next && (next === quote || next === '\\' || (!quote && /\s|["']/.test(next)))) {
        current += next;
        i += 1;
      } else current += char;
      continue;
    }
    if (quote) {
      if (char === quote) quote = '';
      else current += char;
      continue;
    }
    if (char === '"' || char === "'") {
      quote = char;
      continue;
    }
    if (/\s/.test(char)) {
      if (current) {
        tokens.push(current);
        current = '';
      }
      continue;
    }
    current += char;
  }
  if (quote) throw new Error('Custom command has an unterminated quote.');
  if (current) tokens.push(current);
  return tokens;
}

function applyCommandTemplate(value, replacements) {
  return String(value).replace(/\{\{\s*(model|plan_file|plan_text|root)\s*\}\}/g, (_, key) => replacements[key] ?? '');
}

function buildExecutorCommand(args, root, planPath, planText) {
  const agent = String(args.agent ?? 'opencode').trim().toLowerCase();
  const model = String(args.model ?? process.env.CODEXPRO_AGENT_MODEL ?? '').trim();
  const replacements = { model, plan_file: planPath, plan_text: planText, root };

  if (args.command) {
    const template = String(args.command);
    if (!/\{\{\s*(plan_file|plan_text)\s*\}\}/.test(template)) {
      throw new Error('Custom --command must include {{plan_file}} or {{plan_text}} so the agent receives the handoff.');
    }
    const parts = splitCommandTemplate(template).map((part) => applyCommandTemplate(part, replacements));
    const displayParts = splitCommandTemplate(template).map((part) => applyCommandTemplate(part, { ...replacements, plan_text: '<plan_text>' }));
    if (!parts.length) throw new Error('Custom --command is empty.');
    return { agent, model, command: parts[0], args: parts.slice(1), displayArgs: displayParts.slice(1), custom: true };
  }

  if (agent === 'opencode') {
    return { agent, model, command: 'opencode', args: ['run', ...(model ? ['--model', model] : []), planText], displayArgs: ['run', ...(model ? ['--model', model] : []), '<plan_text>'], custom: false };
  }
  if (agent === 'pi') {
    return { agent, model, command: 'pi', args: [...(model ? ['--model', model] : []), '-p', planText], displayArgs: [...(model ? ['--model', model] : []), '-p', '<plan_text>'], custom: false };
  }
  if (agent === 'codex') {
    return { agent, model, command: 'codex', args: ['exec', ...(model ? ['--model', model] : []), planText], displayArgs: ['exec', ...(model ? ['--model', model] : []), '<plan_text>'], custom: false };
  }
  if (agent === 'custom') throw new Error('Custom agent execution requires --command.');
  throw new Error(`Unsupported --agent ${agent}. Use opencode, pi, codex, or custom with --command.`);
}

function executorCommandPreview(commandInfo) {
  return shellCommandPreview([commandInfo.command, ...(commandInfo.displayArgs ?? commandInfo.args)]);
}

function runProcessCaptured(command, args, options) {
  const { timeoutMs, maxOutputBytes } = options;
  const started = Date.now();
  return new Promise((resolve) => {
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: { ...process.env, NO_COLOR: '1' },
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: false,
    });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill('SIGTERM');
      setTimeout(() => {
        if (!child.killed) child.kill('SIGKILL');
      }, 1500).unref();
    }, timeoutMs);
    timer.unref();

    child.stdout.on('data', (chunk) => {
      stdout += String(chunk);
      if (Buffer.byteLength(stdout, 'utf8') > maxOutputBytes * 2) child.kill('SIGTERM');
    });
    child.stderr.on('data', (chunk) => {
      stderr += String(chunk);
      if (Buffer.byteLength(stderr, 'utf8') > maxOutputBytes * 2) child.kill('SIGTERM');
    });
    child.on('error', (error) => {
      clearTimeout(timer);
      resolve({ exitCode: 127, signal: null, durationMs: Date.now() - started, timedOut, stdout: '', stderr: error instanceof Error ? error.message : String(error), spawnError: true });
    });
    child.on('close', (exitCode, signal) => {
      clearTimeout(timer);
      const out = trimBytes(stdout, maxOutputBytes);
      const err = trimBytes(`${stderr}${timedOut ? `\n[codexpro] Command timed out after ${timeoutMs} ms.` : ''}`, maxOutputBytes);
      resolve({ exitCode, signal, durationMs: Date.now() - started, timedOut, stdout: out.text, stderr: err.text, truncated: out.truncated || err.truncated, spawnError: false });
    });
  });
}

function readGitDiff(root, maxBytes) {
  const result = spawnSync('git', ['diff', '--no-ext-diff', '--'], {
    cwd: root,
    encoding: 'utf8',
    maxBuffer: Math.max(maxBytes * 2, 1_000_000),
    shell: false,
  });
  if (result.status !== 0) {
    const reason = result.stderr || result.stdout || `git diff exited ${result.status}`;
    return `# git diff unavailable\n\n${redactForLog(reason).trim()}\n`;
  }
  const diff = result.stdout || '';
  return diff.trim() ? trimBytes(diff, maxBytes).text : '';
}

function codeBlock(label, value) {
  return `## ${label}\n\n\`\`\`text\n${String(value || '').replace(/\`\`\`/g, '\`\\\`\\\`') || '(empty)'}\n\`\`\`\n`;
}

function writeExecutionOutputs(root, contextDir, commandInfo, result, diffText) {
  const bridgeDir = resolveWorkspaceFile(root, contextDir);
  fs.mkdirSync(bridgeDir, { recursive: true, mode: 0o700 });
  const statusPath = path.join(bridgeDir, 'agent-status.md');
  const diffPath = path.join(bridgeDir, 'implementation-diff.patch');
  const logPath = path.join(bridgeDir, 'execution-log.jsonl');
  const commandText = executorCommandPreview(commandInfo);
  const status = [
    '# Agent Execution Status',
    '',
    `Updated: ${new Date().toISOString()}`,
    `Agent: ${commandInfo.agent}`,
    commandInfo.model ? `Model: ${commandInfo.model}` : '',
    `Command: ${commandText}`,
    `Exit code: ${result.exitCode ?? 'null'}`,
    result.signal ? `Signal: ${result.signal}` : '',
    `Timed out: ${result.timedOut ? 'yes' : 'no'}`,
    `Duration: ${result.durationMs} ms`,
    `Diff path: ${path.posix.join(contextDir, 'implementation-diff.patch')}`,
    `Execution log: ${path.posix.join(contextDir, 'execution-log.jsonl')}`,
    '',
    codeBlock('Stdout excerpt', result.stdout),
    codeBlock('Stderr excerpt', result.stderr),
  ].filter(Boolean).join('\n');
  fs.writeFileSync(statusPath, status, { mode: 0o600 });
  fs.writeFileSync(diffPath, diffText || '', { mode: 0o600 });
  const logEvent = {
    ts: new Date().toISOString(),
    event: 'execute_handoff',
    agent: commandInfo.agent,
    model: commandInfo.model || undefined,
    command: commandText,
    exit_code: result.exitCode,
    signal: result.signal,
    timed_out: result.timedOut,
    duration_ms: result.durationMs,
    stdout_excerpt: result.stdout,
    stderr_excerpt: result.stderr,
    diff_path: path.posix.join(contextDir, 'implementation-diff.patch'),
    status_path: path.posix.join(contextDir, 'agent-status.md'),
  };
  fs.appendFileSync(logPath, `${JSON.stringify(logEvent)}\n`, { mode: 0o600 });
  return { statusPath, diffPath, logPath };
}

async function confirmLocalExecution(args, root, commandInfo) {
  if (args.yes) return true;
  if (!process.stdin.isTTY || !process.stdout.isTTY) {
    throw new Error('Use --yes to execute a local handoff in non-interactive shells, or use --dry-run to preview.');
  }
  printBox('Confirm local execution', [
    labelValue('Workspace', root),
    labelValue('Agent', commandInfo.agent),
    ...(commandInfo.model ? [labelValue('Model', commandInfo.model)] : []),
    labelValue('Command', executorCommandPreview(commandInfo)),
    'This runs a local process in the workspace. CodexPro will collect status, logs, and git diff into .ai-bridge.',
  ]);
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try {
    const answer = await ask(rl, 'Run this local agent now?', 'no');
    return ['y', 'yes'].includes(answer.trim().toLowerCase());
  } finally {
    rl.close();
  }
}

function loadHandoffExecution(args) {
  const root = args.root;
  const contextDir = contextDirFromArgs(args);
  const bridgeDir = resolveWorkspaceFile(root, contextDir);
  const planPath = path.join(bridgeDir, 'current-plan.md');
  const maxReadBytes = numberOption(process.env.CODEXPRO_MAX_READ_BYTES, 180_000, 4_000, 2_000_000);
  const maxOutputBytes = numberOption(args.maxOutputBytes ?? process.env.CODEXPRO_MAX_OUTPUT_BYTES, 120_000, 4_000, 2_000_000);
  const timeoutMs = numberOption(args.timeoutMs ?? args.timeout, 600_000, 1_000, 24 * 60 * 60_000);
  if (!fs.existsSync(planPath)) throw new Error(`No handoff plan found at ${path.relative(root, planPath)}. Ask ChatGPT to call handoff_to_agent first.`);
  const planText = readTextFileBounded(planPath, maxReadBytes);
  const commandInfo = buildExecutorCommand(args, root, planPath, planText);
  return { root, contextDir, bridgeDir, planPath, planText, commandInfo, commandText: executorCommandPreview(commandInfo), maxOutputBytes, timeoutMs };
}

function printHandoffDryRun(request, title = 'CodexPro execute-handoff dry run') {
  printBox(title, [
    labelValue('Workspace', request.root),
    labelValue('Plan', path.relative(request.root, request.planPath)),
    labelValue('Agent', request.commandInfo.agent),
    ...(request.commandInfo.model ? [labelValue('Model', request.commandInfo.model)] : []),
    labelValue('Command', request.commandText),
    'No command was executed and no .ai-bridge result files were changed.',
  ]);
}

async function executeHandoffRequest(request, args, options) {
  const confirmed = options.skipConfirmation ? true : await confirmLocalExecution(args, request.root, request.commandInfo);
  if (!confirmed) {
    statusLine('warn', 'Execution cancelled.');
    return { cancelled: true, result: null, outputs: null };
  }
  if (!options.commandAvailableFromRoot(request.commandInfo.command, request.root)) {
    throw new Error(`${request.commandInfo.command} was not found. Install it, add it to PATH, pass an absolute path, or use --command.`);
  }
  statusLine('wait', `Running ${request.commandInfo.agent}: ${request.commandText}`);
  const result = await runProcessCaptured(request.commandInfo.command, request.commandInfo.args, { cwd: request.root, timeoutMs: request.timeoutMs, maxOutputBytes: request.maxOutputBytes });
  const outputs = writeExecutionOutputs(request.root, request.contextDir, request.commandInfo, result, readGitDiff(request.root, request.maxOutputBytes));
  statusLine(result.exitCode === 0 ? 'ok' : 'warn', `Agent exited with code ${result.exitCode ?? 'null'}${result.signal ? ` signal=${result.signal}` : ''}`);
  console.log(`Status: ${path.relative(request.root, outputs.statusPath)}`);
  console.log(`Diff:   ${path.relative(request.root, outputs.diffPath)}`);
  console.log(`Log:    ${path.relative(request.root, outputs.logPath)}`);
  return { cancelled: false, result, outputs };
}

function requireDependencies(dependencies) {
  if (typeof dependencies?.commandAvailableFromRoot !== 'function') throw new Error('Handoff execution command resolver is unavailable.');
  return dependencies;
}

export async function runExecuteHandoff(args, dependencies) {
  const runtime = requireDependencies(dependencies);
  const request = loadHandoffExecution(args);
  if (args.dryRun) {
    printHandoffDryRun(request);
    return;
  }
  const execution = await executeHandoffRequest(request, args, { ...runtime, skipConfirmation: false });
  if (execution.result?.exitCode && execution.result.exitCode !== 0) process.exitCode = execution.result.exitCode;
}

function planHash(planText) {
  return createHash('sha256').update(planText).digest('hex');
}

function isScaffoldedHandoffPlan(planText) {
  return String(planText).trim() === '# Current Plan\n\nNo plan written yet.';
}

function readWatchState(statePath) {
  try {
    const parsed = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function writeWatchState(statePath, state) {
  fs.mkdirSync(path.dirname(statePath), { recursive: true, mode: 0o700 });
  fs.writeFileSync(statePath, `${JSON.stringify(state, null, 2)}\n`, { mode: 0o600 });
}

function appendBridgeLog(root, contextDir, event) {
  const bridgeDir = resolveWorkspaceFile(root, contextDir);
  fs.mkdirSync(bridgeDir, { recursive: true, mode: 0o700 });
  fs.appendFileSync(path.join(bridgeDir, 'execution-log.jsonl'), `${JSON.stringify({ ts: new Date().toISOString(), ...event })}\n`, { mode: 0o600 });
}

async function waitForStablePlan(planPath, debounceMs) {
  try {
    const before = fs.statSync(planPath);
    await sleep(debounceMs);
    const after = fs.statSync(planPath);
    return before.isFile() && after.isFile() && before.size === after.size && before.mtimeMs === after.mtimeMs;
  } catch {
    return false;
  }
}

async function confirmWatchHandoff(args, root) {
  if (args.yes || args.noConfirm) return true;
  if (!process.stdin.isTTY || !process.stdout.isTTY) throw new Error('Use --yes to start watch-handoff in non-interactive shells.');
  printBox('Confirm handoff watcher', [
    labelValue('Workspace', root),
    labelValue('Agent', args.agent ?? 'opencode'),
    ...(args.model ? [labelValue('Model', args.model)] : []),
    'This starts a local-only watcher. Each new .ai-bridge/current-plan.md hash runs through the configured local agent.',
    'ChatGPT only writes the handoff plan; this terminal-owned process performs execution.',
  ]);
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  try {
    const answer = await ask(rl, 'Start automatic local handoff execution?', 'no');
    return ['y', 'yes'].includes(answer.trim().toLowerCase());
  } finally {
    rl.close();
  }
}

export async function runWatchHandoff(args, dependencies) {
  const runtime = requireDependencies(dependencies);
  const root = args.root;
  const contextDir = contextDirFromArgs(args);
  const bridgeDir = resolveWorkspaceFile(root, contextDir);
  const planPath = path.join(bridgeDir, 'current-plan.md');
  const statePath = resolveWorkspaceFile(root, args.stateFile ?? path.posix.join(contextDir, 'watch-handoff-state.json'));
  const pollIntervalMs = numberOption(args.pollIntervalMs ?? args.pollInterval, 2000, 250, 60_000);
  const debounceMs = numberOption(args.debounceMs, 500, 0, 30_000);
  let state = readWatchState(statePath);
  let lastDryRunHash = state.lastPlanHash ?? '';
  let lastSkippedHash = '';
  let stopped = false;

  if (!args.dryRun && !(await confirmWatchHandoff(args, root))) {
    statusLine('warn', 'Watcher cancelled.');
    return;
  }

  printBox('CodexPro watch-handoff', [
    labelValue('Workspace', root),
    labelValue('Plan', path.relative(root, planPath)),
    labelValue('State', path.relative(root, statePath)),
    labelValue('Agent', args.agent ?? 'opencode'),
    ...(args.model ? [labelValue('Model', args.model)] : []),
    labelValue('Poll', `${pollIntervalMs} ms`),
    labelValue('Debounce', `${debounceMs} ms`),
    args.once ? 'Mode: check once and exit.' : 'Mode: watching until Ctrl+C.',
  ]);

  const stop = () => {
    stopped = true;
    statusLine('warn', 'Stopping handoff watcher...');
  };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);

  while (!stopped) {
    if (!fs.existsSync(planPath)) {
      if (args.once) throw new Error(`No handoff plan found at ${path.relative(root, planPath)}.`);
      await sleep(pollIntervalMs);
      continue;
    }
    if (!(await waitForStablePlan(planPath, debounceMs))) {
      if (args.once) throw new Error(`Handoff plan did not become stable at ${path.relative(root, planPath)}.`);
      await sleep(pollIntervalMs);
      continue;
    }

    const request = loadHandoffExecution({ ...args, root, contextDir });
    const currentHash = planHash(request.planText);
    if (isScaffoldedHandoffPlan(request.planText)) {
      if (lastSkippedHash !== currentHash) statusLine('wait', 'Ignoring scaffolded empty handoff plan.');
      lastSkippedHash = currentHash;
      if (args.once) return;
      await sleep(pollIntervalMs);
      continue;
    }
    if (state.lastPlanHash === currentHash || lastDryRunHash === currentHash) {
      statusLine(args.once ? 'ok' : 'wait', `No new handoff plan: ${currentHash.slice(0, 12)}`);
      if (args.once) return;
      await sleep(pollIntervalMs);
      continue;
    }
    if (args.dryRun) {
      printHandoffDryRun(request, 'CodexPro watch-handoff dry run');
      lastDryRunHash = currentHash;
      if (args.once) return;
      await sleep(pollIntervalMs);
      continue;
    }

    appendBridgeLog(root, contextDir, { event: 'watch_handoff_started', plan_hash: currentHash, agent: request.commandInfo.agent, model: request.commandInfo.model || undefined, plan_path: path.posix.join(contextDir, 'current-plan.md') });
    const execution = await executeHandoffRequest(request, { ...args, yes: true }, { ...runtime, skipConfirmation: true });
    const exitCode = execution.result?.exitCode ?? null;
    state = { lastPlanHash: currentHash, lastRanAt: new Date().toISOString(), agent: request.commandInfo.agent, model: request.commandInfo.model || undefined, exitCode, planPath: path.posix.join(contextDir, 'current-plan.md') };
    writeWatchState(statePath, state);
    appendBridgeLog(root, contextDir, { event: 'watch_handoff_finished', plan_hash: currentHash, agent: request.commandInfo.agent, model: request.commandInfo.model || undefined, exit_code: exitCode, status_path: path.posix.join(contextDir, 'agent-status.md'), diff_path: path.posix.join(contextDir, 'implementation-diff.patch') });

    if (args.once) {
      if (exitCode && exitCode !== 0) process.exitCode = exitCode;
      return;
    }
    await sleep(pollIntervalMs);
  }
}
