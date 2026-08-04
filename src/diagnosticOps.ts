import fs from "node:fs/promises";
import type { Dirent, Stats } from "node:fs";
import type { FileHandle } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import initSqlJs, { type SqlDatabase, type SqlJsStatic } from "sql.js";
import { redactStructured } from "./redact.js";

const MAX_RESPONSE_BYTES = 64 * 1024;
const MAX_RECORDS = 100;
const MAX_TABLES = 32;
const MAX_COLUMNS = 64;
const MAX_DATABASE_BYTES = 32 * 1024 * 1024;
const OPERATION_TIMEOUT_MS = 3_000;
const CONFIG_FILES = ["config.toml", "config.toml.bak", "config.toml.backup"] as const;
const RUNTIME_CATEGORIES = ["skills", "plugins", "logs", "sessions"] as const;
const FAMILY_PATTERNS = {
  logs: /^logs_[A-Za-z0-9_-]+\.sqlite$/,
  state: /^state_[A-Za-z0-9_-]+\.sqlite$/,
  memories: /^memories_[A-Za-z0-9_-]+\.sqlite$/,
  goals: /^goals_[A-Za-z0-9_-]+\.sqlite$/
} as const;

export type DiagnosticFamilyKind = keyof typeof FAMILY_PATTERNS;
export type DiagnosticSqliteOperation = "summary" | "integrity_check" | "storage_metadata" | "schema" | "row_counts" | "timestamp_ranges";

interface SafeEntry {
  name: string;
  path: string;
  stat: Stats;
}

export interface DiagnosticOperationsOptions {
  /** Internal test seam. Never exposed by connector configuration or MCP inputs. */
  rootForTest?: string;
  /** Internal synthetic-race seam. Never invoked by production callers. */
  beforeExtensionEnumerationForTest?: (category: "skills" | "plugins") => Promise<void> | void;
  /** Internal synthetic-race seam. Never invoked by production callers. */
  beforeFixedFileReadForTest?: (kind: "configuration" | "database") => Promise<void> | void;
}

function result(status: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return boundResponse({ status, ...extra });
}

function boundResponse(value: Record<string, unknown>): Record<string, unknown> {
  const sanitized = redactStructured(value);
  const encoded = JSON.stringify(sanitized);
  if (Buffer.byteLength(encoded, "utf8") <= MAX_RESPONSE_BYTES) return sanitized;
  return { status: "truncated", truncated: true, record_limit: MAX_RECORDS, response_limit_bytes: MAX_RESPONSE_BYTES };
}

function safeFailure(): Record<string, unknown> {
  return result("unavailable", { reason: "diagnostic operation could not safely complete" });
}

function withTimeout<T>(operation: Promise<T>): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  const timeout = new Promise<T>((_, reject) => {
    timer = setTimeout(() => reject(new Error("diagnostic timeout")), OPERATION_TIMEOUT_MS);
  });
  return Promise.race([operation, timeout]).finally(() => timer && clearTimeout(timer));
}

function isWindowsReparseOrLink(stat: Stats): boolean {
  // Node's public lstat API identifies symbolic links and Windows junctions.
  // Other reparse tags are not exposed as a stable Node >=20 classification, so
  // containment and object-identity checks are required as a second boundary.
  return stat.isSymbolicLink();
}

function sameObject(expected: Stats, actual: Stats): boolean {
  return expected.dev === actual.dev && expected.ino === actual.ino && expected.mode === actual.mode;
}

function sameSnapshot(expected: Stats, actual: Stats): boolean {
  return sameObject(expected, actual) && expected.size === actual.size && expected.mtimeMs === actual.mtimeMs;
}

async function openBound(entry: SafeEntry): Promise<FileHandle> {
  const before = await fs.lstat(entry.path);
  if (isWindowsReparseOrLink(before) || !sameSnapshot(entry.stat, before)) throw new Error("identity changed");
  const handle = await fs.open(entry.path, "r");
  try {
    const bound = await handle.stat();
    const after = await fs.lstat(entry.path);
    if (isWindowsReparseOrLink(after) || !sameSnapshot(entry.stat, bound) || !sameSnapshot(entry.stat, after)) {
      throw new Error("identity changed");
    }
    return handle;
  } catch (error) {
    await handle.close();
    throw error;
  }
}

async function assertBound(entry: SafeEntry, handle: FileHandle): Promise<void> {
  const bound = await handle.stat();
  const current = await fs.lstat(entry.path);
  if (isWindowsReparseOrLink(current) || !sameSnapshot(entry.stat, bound) || !sameSnapshot(entry.stat, current)) {
    throw new Error("identity changed");
  }
}

async function readBoundFile(entry: SafeEntry): Promise<Buffer> {
  const handle = await openBound(entry);
  try {
    const content = await handle.readFile();
    await assertBound(entry, handle);
    return content;
  } finally {
    await handle.close();
  }
}

async function withBoundDirectory<T>(entry: SafeEntry, action: (children: Dirent[], handle: FileHandle) => Promise<T>): Promise<T> {
  const handle = await openBound(entry);
  let directory: Awaited<ReturnType<typeof fs.opendir>> | undefined;
  try {
    // Dir.read() enumerates through the opened directory object rather than a
    // later pathname lookup. The separate FileHandle binds that directory to
    // the identity captured during root validation.
    directory = await fs.opendir(entry.path);
    await assertBound(entry, handle);
    const children: Dirent[] = [];
    while (true) {
      await assertBound(entry, handle);
      const child = await directory.read();
      if (!child) break;
      children.push(child);
    }
    await assertBound(entry, handle);
    const value = await action(children, handle);
    await assertBound(entry, handle);
    return value;
  } finally {
    await directory?.close();
    await handle.close();
  }
}

async function safeRoot(root: string): Promise<{ root: string; entries: SafeEntry[] } | undefined> {
  try {
    const rootStat = await fs.lstat(root);
    if (!rootStat.isDirectory() || isWindowsReparseOrLink(rootStat)) return undefined;
    const canonicalRoot = await fs.realpath(root);
    if (path.resolve(canonicalRoot) !== path.resolve(root)) return undefined;
    const rootEntry = { name: ".", path: root, stat: rootStat };
    const listed = await withBoundDirectory(rootEntry, async (entries) => entries);
    const entries: SafeEntry[] = [];
    for (const item of listed) {
      const itemPath = path.join(root, item.name);
      const stat = await fs.lstat(itemPath);
      if (isWindowsReparseOrLink(stat)) return undefined;
      const canonical = await fs.realpath(itemPath);
      if (path.dirname(canonical) !== canonicalRoot) return undefined;
      entries.push({ name: item.name, path: itemPath, stat });
    }
    return { root: canonicalRoot, entries };
  } catch (error: any) {
    if (error?.code === "ENOENT") return { root, entries: [] };
    return undefined;
  }
}

function direct(entry: SafeEntry[], name: string): SafeEntry | undefined {
  return entry.find((candidate) => candidate.name === name);
}

function fixedChild(root: string, entries: SafeEntry[], name: string): SafeEntry | undefined {
  const entry = direct(entries, name);
  if (!entry || path.basename(entry.path) !== name) return undefined;
  return { ...entry, path: path.join(root, name) };
}

function isSafeEntry(value: SafeEntry | Record<string, unknown>): value is SafeEntry {
  return "path" in value && typeof value.path === "string" && "stat" in value;
}

function databaseEntries(entries: SafeEntry[]): SafeEntry[] {
  return entries.filter((entry) => Object.values(FAMILY_PATTERNS).some((pattern) => pattern.test(entry.name)) && entry.stat.isFile());
}

function utc(value: Date): string {
  return value.toISOString();
}

function databaseMetadata(entries: SafeEntry[]): { items: Record<string, unknown>[]; truncated: boolean } {
  const databases: Record<string, unknown>[] = [];
  const matches = databaseEntries(entries);
  for (const entry of matches.slice(0, MAX_RECORDS)) {
    const sidecars = ["wal", "shm"].filter((kind) => direct(entries, `${entry.name}-${kind}`)?.stat.isFile());
    const family = (Object.entries(FAMILY_PATTERNS).find(([, pattern]) => pattern.test(entry.name))?.[0] ?? "unknown") as string;
    databases.push({ family_kind: family, filename: entry.name, coarse_type: "database", bytes: entry.stat.size, modified_utc: utc(entry.stat.mtime), sidecars });
  }
  return { items: databases, truncated: matches.length > MAX_RECORDS };
}

function categorySummary(entries: SafeEntry[]): Record<string, unknown> {
  const categories: Record<string, unknown> = {};
  for (const category of RUNTIME_CATEGORIES) {
    const directory = direct(entries, category);
    if (!directory) categories[category] = { status: "absent", count: 0, bytes: 0 };
    else if (!directory.stat.isDirectory()) categories[category] = { status: "rejected", count: 0, bytes: 0 };
    else categories[category] = { status: "present", count: 1, bytes: directory.stat.size };
  }
  categories.databases = { status: "present", count: databaseEntries(entries).length, bytes: databaseEntries(entries).reduce((sum, entry) => sum + entry.stat.size, 0) };
  categories.configuration = { status: "present", count: CONFIG_FILES.filter((name) => direct(entries, name)?.stat.isFile()).length, bytes: 0 };
  return categories;
}

async function installedExtensions(
  root: string,
  entries: SafeEntry[],
  beforeEnumerationForTest?: (category: "skills" | "plugins") => Promise<void> | void
): Promise<{ status: "ok" | "unavailable"; items: Record<string, unknown>[]; truncated: boolean }> {
  const values: Record<string, unknown>[] = [];
  let truncated = false;
  for (const source of ["skills", "plugins"] as const) {
    const category = fixedChild(root, entries, source);
    if (!category?.stat.isDirectory()) continue;
    try {
      await beforeEnumerationForTest?.(source);
      await withBoundDirectory(category, async (children, categoryHandle) => {
        if (children.length > MAX_RECORDS - values.length) truncated = true;
        for (const child of children.slice(0, MAX_RECORDS - values.length)) {
          await assertBound(category, categoryHandle);
          const childPath = path.join(category.path, child.name);
          if (path.dirname(path.resolve(childPath)) !== path.resolve(category.path)) throw new Error("child escaped parent");
          const stat = await fs.lstat(childPath);
          if (!child.isDirectory() || isWindowsReparseOrLink(stat)) throw new Error("unexpected child");
          const childEntry = { name: child.name, path: childPath, stat };
          const versionEntries = await withBoundDirectory(childEntry, async (entries) => entries);
          await assertBound(category, categoryHandle);
          const versions = versionEntries.filter((entry) => entry.isDirectory()).map((entry) => entry.name).sort();
          values.push({ name: child.name, source, version: versions.length === 1 ? versions[0] : "unknown", location: `${source}/${child.name}`, duplicate_version: versions.length > 1 });
        }
      });
    } catch {
      return { status: "unavailable", items: [], truncated: false };
    }
  }
  return { status: "ok", items: values, truncated };
}

const ALLOWED_CONFIG: Record<string, Record<string, { type: "boolean" | "integer" | "enum"; values?: readonly string[] }>> = {
  general: { telemetry: { type: "boolean" }, max_threads: { type: "integer" }, sandbox_mode: { type: "enum", values: ["off", "workspace-write", "read-only"] } },
  features: { web_search: { type: "boolean" }, image_generation: { type: "boolean" } }
};

function configValue(raw: string, allowed: { type: "boolean" | "integer" | "enum"; values?: readonly string[] }): unknown {
  const value = raw.trim();
  if (allowed.type === "boolean") return value === "true" || value === "false" ? value === "true" : "invalid";
  if (allowed.type === "integer") {
    const parsed = Number(value);
    return Number.isInteger(parsed) && parsed >= 0 && parsed <= 1_000_000 ? parsed : "invalid";
  }
  const unquoted = value.replace(/^"|"$/g, "");
  return allowed.values?.includes(unquoted) ? unquoted : "invalid";
}

function parseConfiguration(text: string): Record<string, unknown> {
  let section = "";
  let recognized = 0;
  let omitted = 0;
  const approved: Record<string, unknown> = {};
  for (const line of text.split(/\r?\n/).slice(0, 4_000)) {
    const sectionMatch = line.match(/^\s*\[([A-Za-z0-9_-]+)\]\s*(?:#.*)?$/);
    if (sectionMatch) { section = sectionMatch[1]; continue; }
    const match = line.match(/^\s*([A-Za-z0-9_-]+)\s*=\s*(.*?)\s*(?:#.*)?$/);
    if (!match) { if (line.trim() && !line.trim().startsWith("#")) omitted += 1; continue; }
    const key = match[1];
    const allow = ALLOWED_CONFIG[section]?.[key];
    if (!allow) { omitted += 1; continue; }
    approved[`${section}.${key}`] = configValue(match[2], allow);
    recognized += 1;
  }
  return { approved, recognized_keys: recognized, omitted_items: omitted };
}

let sqlJs: Promise<SqlJsStatic> | undefined;
function sqlite(): Promise<SqlJsStatic> {
  sqlJs ??= initSqlJs({ locateFile: (file) => path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "node_modules", "sql.js", "dist", file) });
  return sqlJs;
}

function exec(database: SqlDatabase, sql: string): unknown[][] {
  return database.exec(sql)[0]?.values ?? [];
}

function quotedIdentifier(value: string): string {
  return `"${value.replaceAll('"', '""')}"`;
}

function tableNames(database: SqlDatabase): string[] {
  return exec(database, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 32")
    .map((row) => typeof row[0] === "string" ? row[0] : "")
    .filter(Boolean)
    .slice(0, MAX_TABLES);
}

function totalTableCount(database: SqlDatabase): number {
  return Number(exec(database, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")[0]?.[0] ?? 0);
}

async function selectedDatabase(root: string, entries: SafeEntry[], familyKind: DiagnosticFamilyKind): Promise<SafeEntry | Record<string, unknown>> {
  const matches = databaseEntries(entries).filter((entry) => FAMILY_PATTERNS[familyKind].test(entry.name));
  if (!matches.length) return result("missing", { family_kind: familyKind });
  if (matches.length !== 1) return result("ambiguous", { family_kind: familyKind, matches: matches.length });
  if (matches[0].stat.size > MAX_DATABASE_BYTES) return result("unavailable", { family_kind: familyKind, reason: "database exceeds diagnostic size limit" });
  const selected = { ...matches[0], path: path.join(root, matches[0].name) };
  const current = await fs.lstat(selected.path);
  if (!current.isFile() || isWindowsReparseOrLink(current) || current.size !== matches[0].stat.size) return safeFailure();
  return selected;
}

export class CodexDiagnosticOperations {
  private readonly root: string;
  private readonly beforeExtensionEnumerationForTest?: DiagnosticOperationsOptions["beforeExtensionEnumerationForTest"];
  private readonly beforeFixedFileReadForTest?: DiagnosticOperationsOptions["beforeFixedFileReadForTest"];

  constructor(options: DiagnosticOperationsOptions = {}) {
    this.root = options.rootForTest ?? path.join(os.homedir(), ".codex");
    this.beforeExtensionEnumerationForTest = options.beforeExtensionEnumerationForTest;
    this.beforeFixedFileReadForTest = options.beforeFixedFileReadForTest;
  }

  async inventory(): Promise<Record<string, unknown>> {
    return withTimeout(this.inventoryInner()).catch(() => safeFailure());
  }

  private async inventoryInner(): Promise<Record<string, unknown>> {
    const root = await safeRoot(this.root);
    if (!root) return safeFailure();
    if (!root.entries.length) return result("missing", { categories: categorySummary([]), process_locks: { status: "unavailable" } });
    const extensions = await installedExtensions(root.root, root.entries, this.beforeExtensionEnumerationForTest);
    const databases = databaseMetadata(root.entries);
    if (extensions.status === "unavailable") {
      return result("unavailable", {
        categories: categorySummary(root.entries),
        databases: databases.items,
        installed_extensions: { status: "unavailable" },
        process_locks: { status: "unavailable" }
      });
    }
    return result("ok", { categories: categorySummary(root.entries), databases: databases.items, installed_extensions: extensions.items, truncated: databases.truncated || extensions.truncated, process_locks: { status: "unavailable" } });
  }

  async configurationSummary(): Promise<Record<string, unknown>> {
    return withTimeout(this.configurationSummaryInner()).catch(() => safeFailure());
  }

  private async configurationSummaryInner(): Promise<Record<string, unknown>> {
    const root = await safeRoot(this.root);
    if (!root) return safeFailure();
    if (!root.entries.length) return result("missing", { files: [] });
    const files: Record<string, unknown>[] = [];
    for (const name of CONFIG_FILES) {
      const entry = fixedChild(root.root, root.entries, name);
      if (!entry) { files.push({ config_file: name, status: "absent" }); continue; }
      if (!entry.stat.isFile() || entry.stat.size > MAX_RESPONSE_BYTES) return safeFailure();
      await this.beforeFixedFileReadForTest?.("configuration");
      const content = (await readBoundFile(entry)).toString("utf8");
      files.push({ config_file: name, status: "present", ...parseConfiguration(content) });
    }
    return result("ok", { files });
  }

  async sqliteMetadata(familyKind: DiagnosticFamilyKind, operation: DiagnosticSqliteOperation): Promise<Record<string, unknown>> {
    return withTimeout(this.sqliteMetadataInner(familyKind, operation)).catch(() => safeFailure());
  }

  private async sqliteMetadataInner(familyKind: DiagnosticFamilyKind, operation: DiagnosticSqliteOperation): Promise<Record<string, unknown>> {
    const root = await safeRoot(this.root);
    if (!root) return safeFailure();
    const selected = await selectedDatabase(root.root, root.entries, familyKind);
    if (!isSafeEntry(selected)) return selected;
    let database: SqlDatabase | undefined;
    try {
      // sql.js constructs SQLite from this immutable read buffer only; it never opens
      // the runtime file path, so SQLite cannot checkpoint, journal, or mutate it.
      await this.beforeFixedFileReadForTest?.("database");
      const bytes = await readBoundFile(selected);
      database = new (await sqlite()).Database(bytes);
      const tables = tableNames(database);
      const totalTables = totalTableCount(database);
      const base = { status: "ok", family_kind: familyKind, database: { filename: selected.name, bytes: selected.stat.size, sidecars: ["wal", "shm"].filter((kind) => direct(root.entries, `${selected.name}-${kind}`)?.stat.isFile()) } };
      if (operation === "summary") return result("ok", { ...base, table_count: Math.min(totalTables, MAX_TABLES), truncated_tables: totalTables > MAX_TABLES });
      if (operation === "storage_metadata") return result("ok", { ...base, table_count: Math.min(totalTables, MAX_TABLES), truncated_tables: totalTables > MAX_TABLES, storage_mode: "memory_copy_read_only" });
      if (operation === "integrity_check") {
        const rows = exec(database, "PRAGMA integrity_check");
        return result("ok", { ...base, integrity: rows.length === 1 && rows[0][0] === "ok" ? "ok" : "failed" });
      }
      if (operation === "schema") {
        const schema = tables.map((table, index) => {
          const columns = exec(database!, `PRAGMA table_info(${quotedIdentifier(table)})`).slice(0, MAX_COLUMNS);
          return { table_index: index + 1, column_count: columns.length, truncated_columns: columns.length >= MAX_COLUMNS };
        });
        return result("ok", { ...base, tables: schema, truncated_tables: totalTables > MAX_TABLES });
      }
      if (operation === "row_counts") {
        const counts = tables.map((table, index) => ({ table_index: index + 1, row_count: Number(exec(database!, `SELECT COUNT(*) FROM ${quotedIdentifier(table)}`)[0]?.[0] ?? 0) }));
        return result("ok", { ...base, tables: counts, truncated_tables: totalTables > MAX_TABLES });
      }
      const ranges = tables.map((table, index) => {
        const columns = exec(database!, `PRAGMA table_info(${quotedIdentifier(table)})`).slice(0, MAX_COLUMNS);
        const timestampColumns = columns.filter((column) => typeof column[1] === "string" && /(?:time|date|created|updated|at)$/i.test(column[1]));
        const rangeDataPresent = timestampColumns.some((column) =>
          Number(exec(database!, `SELECT COUNT(*) FROM ${quotedIdentifier(table)} WHERE ${quotedIdentifier(String(column[1]))} IS NOT NULL`)[0]?.[0] ?? 0) > 0
        );
        return { table_index: index + 1, timestamp_column_count: timestampColumns.length, range_data_present: rangeDataPresent };
      });
      return result("ok", { ...base, tables: ranges, truncated_tables: totalTables > MAX_TABLES });
    } catch {
      return result("unavailable", { family_kind: familyKind, reason: "database metadata could not be safely read" });
    } finally {
      database?.close();
    }
  }
}
