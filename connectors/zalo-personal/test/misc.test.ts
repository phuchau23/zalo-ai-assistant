import { describe, expect, it } from "vitest";
import { ConfigError, loadConfig } from "../src/config.ts";
import { fakeTestProxy, testProxy, zcaNetworkOptions, type FetchLike } from "../src/proxy.ts";
import { startApp } from "../src/main.ts";
import { signRequest } from "../src/hmac.ts";
import { parseSyncResponse } from "../src/api-client.ts";
import { SESSION, silentLog, stubSpawner } from "./helpers.ts";

describe("config", () => {
  it("refuses to start without a ≥32-char shared secret", () => {
    expect(() => loadConfig({})).toThrow(ConfigError);
    expect(() => loadConfig({ REMI_SHARED_SECRET: "short" })).toThrow(/at least 32/);
  });

  it("applies defaults", () => {
    const c = loadConfig({ REMI_SHARED_SECRET: "x".repeat(32) });
    expect(c).toMatchObject({
      port: 4100,
      host: "127.0.0.1",
      apiUrl: "http://127.0.0.1:4001",
      mode: "real",
      syncIntervalMs: 60_000,
      logLevel: "info",
    });
    expect(c.instanceId).toMatch(/-\d+$/);
  });

  it("rejects bad mode", () => {
    expect(() => loadConfig({ REMI_SHARED_SECRET: "x".repeat(32), REMI_MODE: "prod" })).toThrow(ConfigError);
  });
});

describe("proxy", () => {
  it("without proxy returns no options (never polyfill: undefined, which would erase zca-js's fetch)", () => {
    const o = zcaNetworkOptions(null);
    expect(o).toEqual({});
    expect("polyfill" in o).toBe(false);
  });

  it("with proxy returns an agent and the node-fetch polyfill", () => {
    const o = zcaNetworkOptions("http://user:pass@127.0.0.1:3128");
    expect(o.agent).toBeDefined();
    expect(typeof o.polyfill).toBe("function");
    expect(o.polyfill).not.toBe(globalThis.fetch);
  });

  it("rejects non-http proxy schemes", () => {
    expect(() => zcaNetworkOptions("socks5://h:1")).toThrow();
  });

  it("testProxy fetches the IP echo through the proxy agent and reports exit IP + latency", async () => {
    let t = 1000;
    const seen: { url: string; hasAgent: boolean }[] = [];
    const fetchImpl: FetchLike = async (url, init) => {
      seen.push({ url, hasAgent: !!init.agent });
      t += 37;
      return { ok: true, status: 200, json: async () => ({ ip: "203.0.113.5" }) };
    };
    const r = await testProxy("http://u:p@proxy:8080", { fetchImpl, now: () => t });
    expect(r).toEqual({ ok: true, exitIp: "203.0.113.5", latencyMs: 37 });
    expect(seen).toEqual([{ url: "https://api.ipify.org?format=json", hasAgent: true }]);
  });

  it("testProxy failure gives a short code, never the proxy URL", async () => {
    const fetchImpl: FetchLike = async () => {
      throw Object.assign(new Error("connect ECONNREFUSED http://u:secretpw@proxy:8080"), { code: "ECONNREFUSED" });
    };
    const r = await testProxy("http://u:secretpw@proxy:8080", { fetchImpl });
    expect(r.ok).toBe(false);
    expect(r.error).toBe("ECONNREFUSED");
    expect(JSON.stringify(r)).not.toContain("secretpw");
    const r407 = await testProxy("http://u:p@proxy:8080", {
      fetchImpl: async () => ({ ok: false, status: 407, json: async () => ({}) }),
    });
    expect(r407).toMatchObject({ ok: false, error: "http_407" });
    expect((await testProxy("not a url")).error).toBe("invalid_proxy_url");
  });

  it("fake proxy test is deterministic", () => {
    expect(fakeTestProxy("http://u:p@good.local:1")).toEqual(fakeTestProxy("http://u:p@good.local:1"));
    expect(fakeTestProxy("http://u:p@good.local:1").ok).toBe(true);
    expect(fakeTestProxy("http://u:p@fail.local:1")).toMatchObject({ ok: false });
  });
});

describe("sync response parsing", () => {
  it("keeps valid accounts and empties the list when the lease is not granted", () => {
    expect(
      parseSyncResponse({
        leaseGranted: true,
        accounts: [
          { connectionId: "a", session: SESSION, proxyUrl: null },
          { connectionId: "b", session: { cookie: [] } }, // invalid session → skipped
          { connectionId: "c", session: SESSION, proxyUrl: "http://u:p@h:1" },
        ],
      }),
    ).toEqual({
      leaseGranted: true,
      accounts: [
        { connectionId: "a", session: SESSION, proxyUrl: null },
        { connectionId: "c", session: SESSION, proxyUrl: "http://u:p@h:1" },
      ],
    });
    expect(parseSyncResponse({ leaseGranted: false, accounts: [{ connectionId: "a", session: SESSION }] })).toEqual({
      leaseGranted: false,
      accounts: [],
    });
    expect(parseSyncResponse({})).toBeNull();
  });
});

describe("real mode HTTP", () => {
  it("/dev/simulate is 404 in real mode; start returns 202 and is idempotent", async () => {
    const secret = "real-mode-secret-0123456789-abcdefg";
    const { spawn, workers } = stubSpawner();
    const app = await startApp(
      {
        port: 0,
        host: "127.0.0.1",
        apiUrl: "http://api.internal:4001",
        sharedSecret: secret,
        mode: "real",
        instanceId: "r",
        syncIntervalMs: 60_000,
        logLevel: "error",
      },
      {
        log: silentLog,
        spawn,
        fetchFn: async () => ({ status: 200, text: async () => JSON.stringify({ leaseGranted: true, accounts: [] }) }),
      },
    );
    try {
      const post = async (path: string, body: unknown) => {
        const raw = JSON.stringify(body);
        return fetch(`http://127.0.0.1:${app.port}${path}`, {
          method: "POST",
          headers: { "x-remi-signature": signRequest(secret, "POST", path, raw), "content-type": "application/json" },
          body: raw,
        });
      };
      expect((await post("/dev/simulate", { action: "incoming", connectionId: "a" })).status).toBe(404);
      expect((await post("/accounts/conn-1/start", { session: SESSION, proxyUrl: null })).status).toBe(202);
      expect((await post("/accounts/conn-1/start", { session: SESSION, proxyUrl: null })).status).toBe(202);
      expect(workers).toHaveLength(1);
      expect(workers[0]!.init).toMatchObject({ role: "account", connectionId: "conn-1", mode: "real" });
      expect((await post("/accounts/conn-1/start", { session: { cookie: [] }, proxyUrl: null })).status).toBe(400);
      expect((await post("/accounts/conn-1/stop", {})).status).toBe(204);
      expect(workers[0]!.exited).toBe(true);
    } finally {
      await app.close();
    }
  });
});
