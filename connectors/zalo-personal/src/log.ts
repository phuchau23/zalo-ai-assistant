// JSON-lines logger. Two layers of protection (CONTRACT §6, plan §11):
// 1. Allow-list of field names: anything else passed in is dropped, so a cookie/text/proxyUrl
//    can never reach the output by accident through an unknown key.
// 2. Scrubbing of every string value: registered secrets (shared secret, proxy URLs, imei,
//    userAgent...) and phone-like digit runs are replaced before writing.

export type LogLevel = "debug" | "info" | "warn" | "error";

export type LogFields = {
  connectionId?: string;
  loginId?: string;
  code?: number | string | null;
  status?: number;
  state?: string;
  kind?: string;
  count?: number;
  attempt?: number;
  delayMs?: number;
  durationMs?: number;
  pid?: number;
  exitCode?: number | null;
  signal?: string | null;
  errName?: string;
  errCode?: string | number | null;
  instance?: string;
  mode?: string;
  host?: string;
  port?: number;
  method?: string;
  path?: string;
  eventType?: string;
  dropped?: number;
  role?: string;
};

const ALLOWED_FIELDS: ReadonlySet<string> = new Set([
  "connectionId",
  "loginId",
  "code",
  "status",
  "state",
  "kind",
  "count",
  "attempt",
  "delayMs",
  "durationMs",
  "pid",
  "exitCode",
  "signal",
  "errName",
  "errCode",
  "instance",
  "mode",
  "host",
  "port",
  "method",
  "path",
  "eventType",
  "dropped",
  "role",
]);

const LEVEL_ORDER: Record<LogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 };

const secrets = new Set<string>();

/** Register a value that must never appear in logs (e.g. proxyUrl, imei, shared secret). */
export function registerSecret(value: string | null | undefined): void {
  if (typeof value === "string" && value.length >= 4) secrets.add(value);
}

/** Test helper. */
export function clearSecrets(): void {
  secrets.clear();
}

// 8+ digit runs (phones, ids embedded in free text) — connection ids are uuids with hex groups ≤ 12
// but are only ever logged in their own field, which is scrubbed separately below.
const PHONE_RE = /(?:\+?84|0)\d{8,10}\b/g;
const LONG_DIGITS_RE = /\d{9,}/g;
const URL_CREDENTIALS_RE = /\b(https?|socks5?):\/\/[^\s/@]+@[^\s]+/gi;

export function scrub(value: string): string {
  let out = value;
  for (const s of secrets) {
    if (out.includes(s)) out = out.split(s).join("[redacted]");
  }
  out = out.replace(URL_CREDENTIALS_RE, "[redacted-url]");
  out = out.replace(PHONE_RE, "[redacted-phone]");
  out = out.replace(LONG_DIGITS_RE, "[redacted-num]");
  return out;
}

const ID_FIELDS: ReadonlySet<string> = new Set(["connectionId", "loginId", "instance", "path"]);

function sanitizeValue(key: string, value: unknown): unknown {
  if (value === null || value === undefined) return value;
  if (typeof value === "number" || typeof value === "boolean") return value;
  if (typeof value === "string") {
    const trimmed = value.length > 200 ? value.slice(0, 200) : value;
    // ids and paths may legitimately contain long digit runs (uuid without dashes, fake uids):
    // only strip registered secrets from them.
    if (ID_FIELDS.has(key)) {
      let out = trimmed;
      for (const s of secrets) if (out.includes(s)) out = out.split(s).join("[redacted]");
      return out;
    }
    return scrub(trimmed);
  }
  return undefined; // objects/arrays are never logged
}

export type Logger = {
  debug(msg: string, fields?: LogFields): void;
  info(msg: string, fields?: LogFields): void;
  warn(msg: string, fields?: LogFields): void;
  error(msg: string, fields?: LogFields): void;
  child(fields: LogFields): Logger;
};

export type LogSink = (line: string) => void;

const defaultSink: LogSink = (line) => {
  process.stdout.write(line + "\n");
};

export function createLogger(
  options: { level?: LogLevel; sink?: LogSink; base?: LogFields } = {},
): Logger {
  const minLevel = LEVEL_ORDER[options.level ?? "info"] ?? LEVEL_ORDER.info;
  const sink = options.sink ?? defaultSink;
  const base = options.base ?? {};

  function write(level: LogLevel, msg: string, fields?: LogFields): void {
    if (LEVEL_ORDER[level] < minLevel) return;
    const record: Record<string, unknown> = {
      ts: new Date().toISOString(),
      level,
      msg: scrub(String(msg)).slice(0, 300),
    };
    const merged: Record<string, unknown> = { ...base, ...(fields ?? {}) };
    for (const [key, value] of Object.entries(merged)) {
      if (!ALLOWED_FIELDS.has(key)) continue;
      const clean = sanitizeValue(key, value);
      if (clean !== undefined) record[key] = clean;
    }
    try {
      sink(JSON.stringify(record));
    } catch {
      // logging must never throw
    }
  }

  return {
    debug: (msg, fields) => write("debug", msg, fields),
    info: (msg, fields) => write("info", msg, fields),
    warn: (msg, fields) => write("warn", msg, fields),
    error: (msg, fields) => write("error", msg, fields),
    child: (fields) => createLogger({ level: options.level, sink, base: { ...base, ...fields } }),
  };
}

/** Safe description of an error: name and code only, never the message (zca-js messages can embed context/cookies). */
export function errFields(err: unknown): { errName: string; errCode: string | number | null } {
  if (err && typeof err === "object") {
    const e = err as { name?: unknown; code?: unknown; cause?: unknown };
    const name = typeof e.name === "string" ? e.name : "Error";
    let code: string | number | null =
      typeof e.code === "string" || typeof e.code === "number" ? e.code : null;
    if (code === null && e.cause && typeof e.cause === "object") {
      const c = (e.cause as { code?: unknown }).code;
      if (typeof c === "string" || typeof c === "number") code = c;
    }
    return { errName: name.slice(0, 60), errCode: code };
  }
  return { errName: typeof err, errCode: null };
}
