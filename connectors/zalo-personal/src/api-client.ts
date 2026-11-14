// Signed calls from the sidecar to the .NET API (/internal/zalo-personal/*, CONTRACT §4).
// Uses Node's built-in fetch: the API is on the internal network and must never go through a nick's proxy.
import { signRequest, SIGNATURE_HEADER } from "./hmac.ts";
import { errFields, type Logger } from "./log.ts";
import type { PostResult } from "./event-queue.ts";
import type { LoginProgressState, PersonalEvent, ZaloSessionData } from "./types.ts";

export type ApiResponse = { status: number; body: unknown };

export type FetchFn = (
  url: string,
  init: { method: string; headers: Record<string, string>; body?: string; signal?: AbortSignal },
) => Promise<{ status: number; text(): Promise<string> }>;

export type DesiredAccount = { connectionId: string; session: ZaloSessionData; proxyUrl: string | null };
export type SyncResponse = { leaseGranted: boolean; accounts: DesiredAccount[] };

export type LoginResultOutcome =
  | { kind: "ok"; connectionId: string }
  | { kind: "conflict"; error: string }
  | { kind: "gone" }
  | { kind: "failed"; status?: number };

export class ApiUnavailableError extends Error {
  constructor(message = "api unavailable") {
    super(message);
    this.name = "ApiUnavailableError";
  }
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

function isObject(v: unknown): v is Record<string, unknown> {
  return v !== null && typeof v === "object" && !Array.isArray(v);
}

export function parseSession(v: unknown): ZaloSessionData | null {
  if (!isObject(v)) return null;
  if (v.cookie === undefined || v.cookie === null) return null;
  if (typeof v.imei !== "string" || v.imei.length === 0) return null;
  if (typeof v.userAgent !== "string" || v.userAgent.length === 0) return null;
  return { cookie: v.cookie, imei: v.imei, userAgent: v.userAgent };
}

export function parseSyncResponse(body: unknown): SyncResponse | null {
  if (!isObject(body) || typeof body.leaseGranted !== "boolean") return null;
  const accounts: DesiredAccount[] = [];
  const list = Array.isArray(body.accounts) ? body.accounts : [];
  for (const a of list) {
    if (!isObject(a) || typeof a.connectionId !== "string" || a.connectionId.length === 0) continue;
    const session = parseSession(a.session);
    if (!session) continue;
    const proxyUrl = typeof a.proxyUrl === "string" && a.proxyUrl.length > 0 ? a.proxyUrl : null;
    accounts.push({ connectionId: a.connectionId, session, proxyUrl });
  }
  return { leaseGranted: body.leaseGranted, accounts: body.leaseGranted ? accounts : [] };
}

export class ApiClient {
  private readonly baseUrl: string;
  private readonly secret: string;
  private readonly fetchFn: FetchFn;
  private readonly timeoutMs: number;
  private readonly log: Logger;
  private readonly retryDelaysMs: number[];

  constructor(options: {
    baseUrl: string;
    secret: string;
    log: Logger;
    fetchFn?: FetchFn;
    timeoutMs?: number;
    retryDelaysMs?: number[];
  }) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    this.secret = options.secret;
    this.log = options.log;
    this.fetchFn = options.fetchFn ?? (globalThis.fetch as unknown as FetchFn);
    this.timeoutMs = options.timeoutMs ?? 10_000;
    this.retryDelaysMs = options.retryDelaysMs ?? [1000, 3000, 10_000];
  }

  /** One signed request. Throws ApiUnavailableError on network error/timeout. */
  async call(method: string, path: string, payload?: unknown): Promise<ApiResponse> {
    const url = new URL(this.baseUrl + path);
    const pathAndQuery = url.pathname + url.search;
    const body = payload === undefined ? "" : JSON.stringify(payload);
    const headers: Record<string, string> = {
      [SIGNATURE_HEADER]: signRequest(this.secret, method, pathAndQuery, body),
      accept: "application/json",
    };
    if (body.length > 0) headers["content-type"] = "application/json";
    let res: { status: number; text(): Promise<string> };
    try {
      res = await this.fetchFn(url.toString(), {
        method: method.toUpperCase(),
        headers,
        body: body.length > 0 ? body : undefined,
        signal: AbortSignal.timeout(this.timeoutMs),
      });
    } catch (err) {
      this.log.debug("api call failed", { method, path: url.pathname, ...errFields(err) });
      throw new ApiUnavailableError();
    }
    let parsed: unknown = null;
    try {
      const text = await res.text();
      parsed = text.length > 0 ? JSON.parse(text) : null;
    } catch {
      parsed = null;
    }
    return { status: res.status, body: parsed };
  }

  /** Retries network errors and 5xx with the configured delays. */
  private async callWithRetry(method: string, path: string, payload?: unknown): Promise<ApiResponse> {
    let lastErr: unknown = null;
    for (let attempt = 0; attempt <= this.retryDelaysMs.length; attempt++) {
      try {
        const r = await this.call(method, path, payload);
        if (r.status < 500) return r;
        lastErr = new ApiUnavailableError(`status ${r.status}`);
        if (attempt === this.retryDelaysMs.length) return r;
      } catch (err) {
        lastErr = err;
      }
      if (attempt < this.retryDelaysMs.length) await sleep(this.retryDelaysMs[attempt]!);
    }
    throw lastErr instanceof Error ? lastErr : new ApiUnavailableError();
  }

  async sync(instance: string, running: { connectionId: string; state: string }[]): Promise<SyncResponse> {
    const r = await this.call("POST", "/internal/zalo-personal/sync", { instance, running });
    if (r.status !== 200) throw new ApiUnavailableError(`sync status ${r.status}`);
    const parsed = parseSyncResponse(r.body);
    if (!parsed) throw new ApiUnavailableError("sync bad body");
    return parsed;
  }

  /** "ok" | "gone" (404: loginId no longer exists) | "failed". */
  async loginProgress(body: {
    loginId: string;
    state: LoginProgressState;
    qrPng?: string;
    error?: string;
  }): Promise<"ok" | "gone" | "failed"> {
    try {
      const r = await this.callWithRetry("POST", "/internal/zalo-personal/login-progress", body);
      if (r.status === 404) return "gone";
      if (r.status >= 200 && r.status < 300) return "ok";
      this.log.warn("login-progress rejected", { loginId: body.loginId, status: r.status });
      return "failed";
    } catch {
      this.log.error("login-progress unreachable", { loginId: body.loginId });
      return "failed";
    }
  }

  async loginResult(body: {
    loginId: string;
    uid: string;
    displayName: string;
    avatarUrl?: string | null;
    session: ZaloSessionData;
  }): Promise<LoginResultOutcome> {
    try {
      const r = await this.callWithRetry("POST", "/internal/zalo-personal/login-result", body);
      if (r.status === 200 && isObject(r.body) && typeof r.body.connectionId === "string") {
        return { kind: "ok", connectionId: r.body.connectionId };
      }
      if (r.status === 409) {
        const error = isObject(r.body) && typeof r.body.error === "string" ? r.body.error : "conflict";
        return { kind: "conflict", error };
      }
      if (r.status === 404) return { kind: "gone" };
      return { kind: "failed", status: r.status };
    } catch {
      return { kind: "failed" };
    }
  }

  async postEvents(connectionId: string, events: PersonalEvent[]): Promise<PostResult> {
    let r: ApiResponse;
    try {
      r = await this.call("POST", "/internal/zalo-personal/events", { connectionId, events });
    } catch {
      return { kind: "retry" };
    }
    if (r.status >= 200 && r.status < 300) return { kind: "ok" };
    if (r.status === 404) return { kind: "gone" };
    if (r.status >= 500 || r.status === 408 || r.status === 429) return { kind: "retry", status: r.status };
    return { kind: "drop", status: r.status };
  }

  async postSession(connectionId: string, session: ZaloSessionData): Promise<boolean> {
    try {
      const r = await this.callWithRetry("POST", "/internal/zalo-personal/session", { connectionId, session });
      if (r.status >= 200 && r.status < 300) return true;
      this.log.warn("session update rejected", { connectionId, status: r.status });
      return false;
    } catch {
      this.log.error("session update unreachable", { connectionId });
      return false;
    }
  }
}
