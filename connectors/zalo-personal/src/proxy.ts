// Proxy plumbing (plan §8, CONTRACT §6).
// zca-js sends every HTTP call through `options.polyfill(url, { agent })` and opens the WebSocket
// with `options.agent`. Node's built-in fetch ignores `agent`, so with a proxy we must pass
// node-fetch as the polyfill. Fail-closed: if the proxy does not work we never fall back to a
// direct connection.
import nodeFetch from "node-fetch";
import { HttpsProxyAgent } from "https-proxy-agent";
import type { Agent } from "node:http";

/** Public IP echo service, fetched THROUGH the proxy. Carries no customer data. */
export const IP_ECHO_URL = "https://api.ipify.org?format=json";

export class InvalidProxyError extends Error {
  constructor() {
    super("invalid proxy url");
    this.name = "InvalidProxyError";
  }
}

export function validateProxyUrl(proxyUrl: string): URL {
  let u: URL;
  try {
    u = new URL(proxyUrl);
  } catch {
    throw new InvalidProxyError();
  }
  if ((u.protocol !== "http:" && u.protocol !== "https:") || !u.hostname) throw new InvalidProxyError();
  return u;
}

export type ZcaNetworkOptions = { agent?: Agent; polyfill?: typeof fetch };

/**
 * Options to merge into `new Zalo({...})`. Without a proxy returns `{}` — zca-js then keeps its own
 * default (`global.fetch`). Never pass `polyfill: undefined`: zca-js does Object.assign on its
 * defaults, so an explicit undefined would erase the default fetch.
 */
export function zcaNetworkOptions(proxyUrl: string | null): ZcaNetworkOptions {
  if (!proxyUrl) return {};
  validateProxyUrl(proxyUrl);
  const agent = new HttpsProxyAgent(proxyUrl) as unknown as Agent;
  return { agent, polyfill: nodeFetch as unknown as typeof fetch };
}

export type ProxyTestResult = { ok: boolean; exitIp?: string; latencyMs?: number; error?: string };

/** Short, non-sensitive error label (never the message: it may echo the proxy URL). */
export function proxyErrorLabel(err: unknown): string {
  if (err && typeof err === "object") {
    const e = err as { name?: unknown; code?: unknown; type?: unknown; cause?: unknown };
    if (e.name === "AbortError" || e.name === "TimeoutError" || e.type === "aborted") return "timeout";
    if (typeof e.code === "string" && /^[A-Z_0-9]{2,40}$/.test(e.code)) return e.code;
    if (e.cause && typeof e.cause === "object") {
      const c = (e.cause as { code?: unknown }).code;
      if (typeof c === "string" && /^[A-Z_0-9]{2,40}$/.test(c)) return c;
    }
    if (typeof e.name === "string" && e.name.length <= 40) return e.name;
  }
  return "error";
}

export type FetchLike = (url: string, init: Record<string, unknown>) => Promise<{
  ok: boolean;
  status: number;
  json(): Promise<unknown>;
}>;

export async function testProxy(
  proxyUrl: string,
  options: { timeoutMs?: number; fetchImpl?: FetchLike; now?: () => number } = {},
): Promise<ProxyTestResult> {
  const now = options.now ?? (() => Date.now());
  try {
    validateProxyUrl(proxyUrl);
  } catch {
    return { ok: false, error: "invalid_proxy_url" };
  }
  const fetchImpl = options.fetchImpl ?? (nodeFetch as unknown as FetchLike);
  const started = now();
  try {
    const agent = new HttpsProxyAgent(proxyUrl);
    const res = await fetchImpl(IP_ECHO_URL, {
      agent,
      signal: AbortSignal.timeout(options.timeoutMs ?? 10_000),
      headers: { accept: "application/json" },
    });
    const latencyMs = Math.max(0, now() - started);
    if (!res.ok) return { ok: false, latencyMs, error: `http_${res.status}` };
    const body = (await res.json()) as { ip?: unknown };
    const ip = typeof body?.ip === "string" ? body.ip : undefined;
    if (!ip) return { ok: false, latencyMs, error: "bad_response" };
    return { ok: true, exitIp: ip, latencyMs };
  } catch (err) {
    return { ok: false, latencyMs: Math.max(0, now() - started), error: proxyErrorLabel(err) };
  }
}

/** Deterministic proxy test for REMI_MODE=fake: host containing "fail" fails, others succeed. */
export function fakeTestProxy(proxyUrl: string): ProxyTestResult {
  let u: URL;
  try {
    u = validateProxyUrl(proxyUrl);
  } catch {
    return { ok: false, error: "invalid_proxy_url" };
  }
  if (u.hostname.includes("fail")) return { ok: false, latencyMs: 5, error: "ECONNREFUSED" };
  let h = 0;
  for (const ch of u.hostname) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return { ok: true, exitIp: `203.0.113.${(h % 254) + 1}`, latencyMs: 42 };
}
