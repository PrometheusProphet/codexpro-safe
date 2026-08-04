declare module "sql.js" {
  export interface SqlDatabase {
    exec(sql: string): Array<{ columns: string[]; values: unknown[][] }>;
    close(): void;
  }

  export interface SqlJsStatic {
    Database: new (data?: Uint8Array) => SqlDatabase;
  }

  export default function initSqlJs(options?: { locateFile?: (file: string) => string }): Promise<SqlJsStatic>;
}
