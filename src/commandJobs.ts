import { randomUUID } from "node:crypto";

import type { CommandResult } from "./bashOps.js";

export const COMMAND_SYNC_BUDGET_MS = 90_000;
export const COMMAND_STATUS_WAIT_MAX_MS = 30_000;
const COMMAND_JOB_RETENTION_MS = 10 * 60_000;
const MAX_COMMAND_JOBS = 24;

export type CommandJobState = "running" | "completed" | "failed";

export interface CommandJobRecord {
  id: string;
  workspaceId: string;
  command: string;
  startedAt: number;
  completedAt?: number;
  state: CommandJobState;
  result?: CommandResult;
  error?: unknown;
  settled: Promise<void>;
}

export async function startCommandJobWithBudget(
  registry: CommandJobRegistry,
  workspaceId: string,
  command: string,
  run: () => Promise<CommandResult>,
  syncBudgetMs = COMMAND_SYNC_BUDGET_MS
): Promise<CommandJobRecord> {
  const job = registry.start(workspaceId, command, run);
  await registry.wait(job, syncBudgetMs);
  return job;
}

export class CommandJobRegistry {
  readonly #jobs = new Map<string, CommandJobRecord>();

  start(
    workspaceId: string,
    command: string,
    run: () => Promise<CommandResult>
  ): CommandJobRecord {
    this.prune();
    if (this.#jobs.size >= MAX_COMMAND_JOBS) {
      throw new Error(`Too many retained command jobs (${MAX_COMMAND_JOBS}). Wait for running jobs to settle and retrieve their status before starting another long command.`);
    }

    const record: CommandJobRecord = {
      id: randomUUID(),
      workspaceId,
      command,
      startedAt: Date.now(),
      state: "running",
      settled: Promise.resolve()
    };
    this.#jobs.set(record.id, record);
    record.settled = Promise.resolve()
      .then(run)
      .then(
        (result) => {
          record.result = result;
          record.state = "completed";
          record.completedAt = Date.now();
        },
        (error) => {
          record.error = error;
          record.state = "failed";
          record.completedAt = Date.now();
        }
      );
    return record;
  }

  get(id: string): CommandJobRecord | undefined {
    this.prune();
    return this.#jobs.get(id);
  }

  delete(id: string): void {
    this.#jobs.delete(id);
  }

  async wait(record: CommandJobRecord, waitMs: number): Promise<CommandJobRecord> {
    if (record.state !== "running" || waitMs <= 0) return record;
    let timer: NodeJS.Timeout | undefined;
    try {
      await Promise.race([
        record.settled,
        new Promise<void>((resolve) => {
          timer = setTimeout(resolve, waitMs);
        })
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
    return record;
  }

  prune(now = Date.now()): void {
    for (const [id, record] of this.#jobs) {
      if (record.state !== "running" && record.completedAt && now - record.completedAt > COMMAND_JOB_RETENTION_MS) {
        this.#jobs.delete(id);
      }
    }

    if (this.#jobs.size < MAX_COMMAND_JOBS) return;
    const completed = [...this.#jobs.values()]
      .filter((record) => record.state !== "running")
      .sort((left, right) => (left.completedAt ?? left.startedAt) - (right.completedAt ?? right.startedAt));
    for (const record of completed) {
      if (this.#jobs.size < MAX_COMMAND_JOBS) break;
      this.#jobs.delete(record.id);
    }
  }
}
