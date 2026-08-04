import path from "node:path";
import { fileURLToPath } from "node:url";
import { parentPort, workerData } from "node:worker_threads";
import initSqlJs, { type SqlDatabase, type SqlJsStatic } from "sql.js";

const MAX_TABLES = 32;
const MAX_COLUMNS = 64;
const VALID_OPERATIONS = new Set(["summary", "integrity_check", "storage_metadata", "schema", "row_counts", "timestamp_ranges"]);

interface Work {
  bytes: ArrayBuffer;
  operation: string;
  delayMsForTest: number;
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

async function sqlite(): Promise<SqlJsStatic> {
  return initSqlJs({ locateFile: (file) => path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "node_modules", "sql.js", "dist", file) });
}

async function main(): Promise<void> {
  const work = workerData as Work;
  if (!work || !(work.bytes instanceof ArrayBuffer) || !VALID_OPERATIONS.has(work.operation)) throw new Error("invalid SQLite work");
  if (Number.isInteger(work.delayMsForTest) && work.delayMsForTest > 0) {
    const until = Date.now() + Math.min(work.delayMsForTest, 10_000);
    while (Date.now() < until) { /* deterministic termination seam */ }
  }
  const bytes = new Uint8Array(work.bytes);
  let database: SqlDatabase | undefined;
  try {
    database = new (await sqlite()).Database(bytes);
    const tables = tableNames(database);
    const totalTables = totalTableCount(database);
    if (work.operation === "summary") return parentPort!.postMessage({ table_count: Math.min(totalTables, MAX_TABLES), truncated_tables: totalTables > MAX_TABLES });
    if (work.operation === "storage_metadata") return parentPort!.postMessage({ table_count: Math.min(totalTables, MAX_TABLES), truncated_tables: totalTables > MAX_TABLES, storage_mode: "memory_copy_read_only" });
    if (work.operation === "integrity_check") {
      const rows = exec(database, "PRAGMA integrity_check");
      return parentPort!.postMessage({ integrity: rows.length === 1 && rows[0][0] === "ok" ? "ok" : "failed" });
    }
    if (work.operation === "schema") {
      const schema = tables.map((table, index) => {
        const columns = exec(database!, `PRAGMA table_info(${quotedIdentifier(table)})`).slice(0, MAX_COLUMNS);
        return { table_index: index + 1, column_count: columns.length, truncated_columns: columns.length >= MAX_COLUMNS };
      });
      return parentPort!.postMessage({ tables: schema, truncated_tables: totalTables > MAX_TABLES });
    }
    if (work.operation === "row_counts") {
      const counts = tables.map((table, index) => ({ table_index: index + 1, row_count: Number(exec(database!, `SELECT COUNT(*) FROM ${quotedIdentifier(table)}`)[0]?.[0] ?? 0) }));
      return parentPort!.postMessage({ tables: counts, truncated_tables: totalTables > MAX_TABLES });
    }
    const ranges = tables.map((table, index) => {
      const columns = exec(database!, `PRAGMA table_info(${quotedIdentifier(table)})`).slice(0, MAX_COLUMNS);
      const timestampColumns = columns.filter((column) => typeof column[1] === "string" && /(?:time|date|created|updated|at)$/i.test(column[1]));
      const rangeDataPresent = timestampColumns.some((column) =>
        Number(exec(database!, `SELECT COUNT(*) FROM ${quotedIdentifier(table)} WHERE ${quotedIdentifier(String(column[1]))} IS NOT NULL`)[0]?.[0] ?? 0) > 0
      );
      return { table_index: index + 1, timestamp_column_count: timestampColumns.length, range_data_present: rangeDataPresent };
    });
    parentPort!.postMessage({ tables: ranges, truncated_tables: totalTables > MAX_TABLES });
  } finally {
    database?.close();
    bytes.fill(0);
  }
}

main().catch(() => process.exit(1));
