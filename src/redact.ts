const OPENAI_SECRET_PATTERN = /\bsk-[A-Za-z0-9_-]{10,}\b/g;
const SECRET_ASSIGNMENT_PATTERN = /\b[A-Za-z0-9_]*(?:API[_-]?KEY|TOKEN|SECRET|PASSWORD|PRIVATE[_-]?KEY)[A-Za-z0-9_]*\s*=\s*(?:"[^"\r\n]{12,}"|'[^'\r\n]{12,}'|`[^`\r\n]{12,}`|[A-Za-z0-9_./+=-]{20,})/gi;
const AUTHORIZATION_HEADER_PATTERN = /\b(Authorization\s*:\s*)([A-Za-z]+)\s+([^\s"'`<>]+)/gi;
const JWT_PATTERN = /\b[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/g;
const PEM_PRIVATE_KEY_PATTERN = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----/g;
const LONG_BASE64ISH_PATTERN = /\b(?:[A-Za-z0-9+/]{96,}={0,2}|[A-Za-z0-9_-]{96,})\b/g;
const URL_QUERY_SECRET_PATTERN = /([?&](?:access_token|api_key|codexpro_token|password|secret|token|key)=)([^&#\s"'`<>]+)/gi;
const CREDENTIALED_URL_PATTERN = /\b([A-Za-z][A-Za-z0-9+.-]*:\/\/)([^\/\s:@]+):([^\/\s@]+)@/g;

export interface RedactionResult {
  text: string;
  count: number;
}

export function hasSecretValue(text: string): boolean {
  return redactSensitiveTextWithCount(text).count > 0;
}

export function redactSensitiveText(text: string): string {
  return redactSensitiveTextWithCount(text).text;
}

export function redactSensitiveTextWithCount(text: string): RedactionResult {
  let count = 0;
  let redacted = text;

  const replaceSecret = (pattern: RegExp, replacer: (...args: any[]) => string) => {
    pattern.lastIndex = 0;
    redacted = redacted.replace(pattern, (...args: any[]) => {
      const match = String(args[0] ?? "");
      if (isPlaceholderSecret(match)) return match;
      const replacement = replacer(...args);
      if (replacement !== match) count += 1;
      return replacement;
    });
  };

  replaceSecret(PEM_PRIVATE_KEY_PATTERN, (match) => redactPemBlock(String(match)));
  replaceSecret(SECRET_ASSIGNMENT_PATTERN, (match) => redactSecretAssignment(String(match)));
  replaceSecret(AUTHORIZATION_HEADER_PATTERN, (_match, prefix, scheme) => `${prefix}${scheme} [REDACTED_SECRET]`);
  replaceSecret(CREDENTIALED_URL_PATTERN, (_match, scheme) => `${scheme}[REDACTED_SECRET]@`);
  replaceSecret(URL_QUERY_SECRET_PATTERN, (_match, prefix) => `${prefix}[REDACTED_SECRET]`);
  replaceSecret(JWT_PATTERN, () => "[REDACTED_SECRET]");
  replaceSecret(OPENAI_SECRET_PATTERN, () => "[REDACTED_SECRET]");
  replaceSecret(LONG_BASE64ISH_PATTERN, () => "[REDACTED_SECRET]");

  return { text: redacted, count };
}

export function redactStructured<T>(value: T, depth = 0): T {
  if (depth > 8) return value;
  if (typeof value === "string") return redactSensitiveText(value) as T;
  if (!value || typeof value !== "object") return value;
  if (Array.isArray(value)) return value.map((item) => redactStructured(item, depth + 1)) as T;

  const out: Record<string, unknown> = {};
  for (const [key, item] of Object.entries(value)) {
    out[key] = redactStructured(item, depth + 1);
  }
  return out as T;
}

function isPlaceholderSecret(value: string): boolean {
  const normalized = value.toLowerCase();
  return (
    normalized.includes("[redacted_secret]") ||
    normalized.includes("replace-me") ||
    normalized.includes("your-api-key-here") ||
    normalized.includes("<openai_api_key>") ||
    normalized.includes("<api_key>") ||
    normalized.includes("<token>") ||
    normalized.includes("process.env.") ||
    normalized.includes("import.meta.env.") ||
    normalized.includes("os.environ") ||
    normalized.includes("getenv(") ||
    normalized === "sk-..." ||
    normalized.endsWith("=sk-...")
  );
}

function redactPemBlock(value: string): string {
  const lines = value.replace(/\r\n/g, "\n").split("\n");
  if (lines.length <= 2) return "[REDACTED_SECRET]";
  return lines.map((line, index) => (index === 0 || index === lines.length - 1 ? line : "[REDACTED_SECRET]")).join("\n");
}

function redactSecretAssignment(value: string): string {
  const index = value.indexOf("=");
  if (index < 0) return "[REDACTED_SECRET]";
  return `${value.slice(0, index).trimEnd()}= [REDACTED_SECRET]`;
}
