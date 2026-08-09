import path from "node:path";
import fsp from "node:fs/promises";
import type { CodexProConfig } from "./config.js";
import type { Workspace } from "./guard.js";
import { CodexProError, PathGuard } from "./guard.js";
import { writeTextFile } from "./fsOps.js";
import {
  validatePromptBeforeSave,
  type PromptValidationResult
} from "./promptContractValidation.js";

export type PromptFileTarget = "ai_bridge" | "chatgpt_generated" | "loop_inbox";

const PROMPT_TARGET_DIRS: Record<PromptFileTarget, (config: CodexProConfig) => string> = {
  ai_bridge: (config) => `${trimTrailingSlashes(config.contextDir)}/prompts`,
  chatgpt_generated: () => "docs/chatgpt/generated-prompts",
  loop_inbox: () => "docs/loop/inbox"
};

const ALLOWED_TARGETS = new Set<PromptFileTarget>(["ai_bridge", "chatgpt_generated", "loop_inbox"]);
const ALLOWED_EXTENSIONS = new Set([".md", ".txt"]);
const CONTROL_CHARS = /[\x00-\x1f\x7f]/;
const MAX_ACTIVE_AI_BRIDGE_PROMPTS = 20;
const RETENTION_MANIFEST = ".retain.json";

export interface SavePromptFileInput {
  target?: PromptFileTarget;
  title?: string;
  prompt: string;
  filename?: string;
  overwrite?: boolean;
  contractManifest?: unknown;
}

export interface SavePromptFileResult {
  target: PromptFileTarget;
  path: string;
  bytes: number;
  sha256: string;
  existed: boolean;
  additions: number;
  deletions: number;
  changed: boolean;
  retiredPromptFiles: number;
  validation?: PromptValidationResult;
}

function trimTrailingSlashes(value: string): string {
  return value.replace(/[\\/]+$/, "") || ".";
}

function targetFrom(value: unknown): PromptFileTarget {
  const target = String(value ?? "ai_bridge");
  if (ALLOWED_TARGETS.has(target as PromptFileTarget)) return target as PromptFileTarget;
  throw new CodexProError("target must be one of: ai_bridge, chatgpt_generated, loop_inbox.");
}

function timestampUtc(date = new Date()): string {
  const yyyy = String(date.getUTCFullYear()).padStart(4, "0");
  const mm = String(date.getUTCMonth() + 1).padStart(2, "0");
  const dd = String(date.getUTCDate()).padStart(2, "0");
  const hh = String(date.getUTCHours()).padStart(2, "0");
  const min = String(date.getUTCMinutes()).padStart(2, "0");
  const ss = String(date.getUTCSeconds()).padStart(2, "0");
  return `${yyyy}${mm}${dd}-${hh}${min}${ss}`;
}

function slugFromTitle(title: unknown): string {
  const slug = String(title ?? "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "")
    .slice(0, 80)
    .replace(/^-|-$/g, "");
  return slug || "codex-prompt";
}

function generatedFilename(title: unknown): string {
  return `${timestampUtc()}-${slugFromTitle(title)}.md`;
}

function validateSuppliedFilename(value: string): string {
  if (value !== value.trim()) {
    throw new CodexProError("filename must not contain leading or trailing whitespace.");
  }
  if (!value) throw new CodexProError("filename must not be empty.");
  if (CONTROL_CHARS.test(value)) throw new CodexProError("filename must not contain control characters.");
  if (value.startsWith(".")) throw new CodexProError("hidden prompt filenames are not allowed.");
  if (value.includes("..")) throw new CodexProError("filename must not contain '..'.");
  if (value.includes("/") || value.includes("\\") || value.includes(":")) {
    throw new CodexProError("filename must be a basename only, without path separators or drive prefixes.");
  }
  if (path.isAbsolute(value) || path.posix.isAbsolute(value) || path.win32.isAbsolute(value) || /^[A-Za-z]:/.test(value)) {
    throw new CodexProError("filename must be relative and must not include a drive prefix.");
  }
  if (path.basename(value) !== value || path.posix.basename(value) !== value || path.win32.basename(value) !== value) {
    throw new CodexProError("filename must be a basename only.");
  }

  const ext = path.extname(value).toLowerCase();
  if (!ALLOWED_EXTENSIONS.has(ext)) {
    throw new CodexProError("filename extension must be .md or .txt.");
  }
  const basename = value.slice(0, -ext.length);
  if (!basename) throw new CodexProError("filename basename must not be empty.");
  return value;
}

function promptFilename(input: SavePromptFileInput): string {
  if (input.filename !== undefined) return validateSuppliedFilename(String(input.filename));
  return generatedFilename(input.title);
}

function persistedPromptContent(prompt: string): string {
  return `${prompt.trimEnd()}\n`;
}

async function retainedPromptNames(directory: string): Promise<Set<string>> {
  const retained = new Set<string>(); const manifest = path.join(directory, RETENTION_MANIFEST);
  try {
    const stat = await fsp.lstat(manifest); if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024) return retained;
    const parsed = JSON.parse(await fsp.readFile(manifest, "utf8"));
    if (!parsed || typeof parsed !== "object" || !Array.isArray(parsed.retain) || parsed.retain.length > 100) return retained;
    for (const value of parsed.retain) if (typeof value === "string") retained.add(validateSuppliedFilename(value));
  } catch { /* An absent or invalid optional manifest retains nothing. */ }
  return retained;
}

async function pruneAiBridgePrompts(guard: PathGuard, workspace: Workspace, targetDir: string, savedFilename: string): Promise<number> {
  const directory = guard.resolve(workspace, targetDir, { forWrite: true }).absPath;
  const retained = await retainedPromptNames(directory); retained.add(savedFilename);
  const candidates: Array<{ name: string; modified: number }> = [];
  for (const entry of await fsp.readdir(directory, { withFileTypes: true })) {
    if (!entry.isFile() || entry.isSymbolicLink() || !ALLOWED_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) continue;
    const resolved = guard.resolve(workspace, `${targetDir}/${entry.name}`, { forWrite: true });
    const stat = await fsp.lstat(resolved.absPath); if (!stat.isFile() || stat.isSymbolicLink()) continue;
    candidates.push({ name: entry.name, modified: stat.mtimeMs });
  }
  const fixed = candidates.filter((item) => retained.has(item.name));
  const ordinary = candidates.filter((item) => !retained.has(item.name)).sort((left, right) => right.modified - left.modified || right.name.localeCompare(left.name));
  const keepOrdinary = Math.max(0, MAX_ACTIVE_AI_BRIDGE_PROMPTS - fixed.length); let retired = 0;
  for (const item of ordinary.slice(keepOrdinary)) { await fsp.unlink(guard.resolve(workspace, `${targetDir}/${item.name}`, { forWrite: true }).absPath); retired += 1; }
  return retired;
}

export async function savePromptFile(
  config: CodexProConfig,
  guard: PathGuard,
  workspace: Workspace,
  input: SavePromptFileInput
): Promise<SavePromptFileResult> {
  const prompt = String(input.prompt ?? "");
  if (!prompt.trim()) throw new CodexProError("prompt must not be empty.");
  const content = persistedPromptContent(prompt);
  const validation = await validatePromptBeforeSave(
    guard,
    workspace,
    content,
    input.contractManifest
  );

  const target = targetFrom(input.target);
  const targetDir = PROMPT_TARGET_DIRS[target](config);
  const filename = promptFilename(input);
  const relPath = `${targetDir}/${filename}`;
  const result = await writeTextFile(config, guard, workspace, relPath, content, {
    createDirs: true,
    overwrite: input.overwrite
  });
  const retiredPromptFiles = target === "ai_bridge" ? await pruneAiBridgePrompts(guard, workspace, targetDir, filename) : 0;

  return {
    target,
    path: result.path,
    bytes: result.bytes,
    sha256: result.sha256,
    existed: result.existed,
    additions: result.diff.additions,
    deletions: result.diff.deletions,
    changed: result.diff.changed,
    retiredPromptFiles,
    validation
  };
}
