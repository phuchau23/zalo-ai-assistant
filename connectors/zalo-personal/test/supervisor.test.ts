import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { HEALTHY_RESET_MS, RESTART_DELAYS_MS, RestartBackoff } from "../src/backoff.ts";
import { SentCache } from "../src/sent-cache.ts";
import { Supervisor } from "../src/supervisor.ts";
import type { SyncResponse } from "../src/api-client.ts";
import { MockApi, SESSION, eventsCollector, flush, silentLog, stubSpawner } from "./helpers.ts";

const MIN = 60_000;

function setup(extra: { leaseTtlMs?: number; loginTimeoutMs?: number } = {}) {
  const api = new MockApi();
  const { spawn, workers } = stubSpawner();
  const ev = eventsCollector();
  const sup = new Supervisor({
    instanceId: "inst-1",
    mode: "fake",
    api,
    spawn,
    pushEvent: ev.push,
    sentCache: new SentCache(),
    log: silentLog,
    stopGraceMs: 1000,
    ...extra,
  });
  return { api, sup, workers, events: ev.events };
}

const acc = (connectionId: string, proxyUrl: string | null = null) => ({ connectionId, session: SESSION, proxyUrl });
const granted = (...accounts: ReturnType<typeof acc>[]): SyncResponse => ({ leaseGranted: true, accounts });

describe("RestartBackoff", () => {
  it("follows 1, 5, 15, 30, 30 minutes", () => {
    const b = new RestartBackoff();
    const seq = [0, 1, 2, 3, 4, 5].map(() => b.nextDelay(null, 0));
    expect(seq).toEqual([1, 5, 15, 30, 30, 30].map((m) => m * MIN));
    expect(RESTART_DELAYS_MS).toEqual([1, 5, 15, 30].map((m) => m * MIN));
  });

  it("resets after 10 minutes healthy", () => {
    const b = new RestartBackoff();
    b.nextDelay(null, 0);
    b.nextDelay(null, 0);
    expect(b.nextDelay(0, HEALTHY_RESET_MS - 1)).toBe(15 * MIN); // not long enough
    expect(b.nextDelay(1000, 1000 + HEALTHY_RESET_MS)).toBe(1 * MIN);
  });
});

describe("Supervisor sync reconciliation", () => {
  it("starts missing accounts, keeps running ones, stops extra ones", async () => {
    const { api, sup, workers, events } = setup();
    api.syncResponses.push(granted(acc("a"), acc("b")));
    await sup.syncOnce();
    expect(workers.map((w) => w.init)).toMatchObject([
      { role: "account", connectionId: "a", mode: "fake" },
      { role: "account", connectionId: "b" },
    ]);
    expect(api.syncCalls[0]).toEqual({ instance: "inst-1", running: [] });

    workers[0]!.emit({ type: "ready" });
    workers[0]!.emit({ type: "event", event: { type: "state", state: "connected", code: null, detail: null, ts: 1 } });

    api.syncResponses.push(granted(acc("a"), acc("c")));
    await sup.syncOnce();
    expect(api.syncCalls[1]!.running).toEqual([
      { connectionId: "a", state: "connected" },
      { connectionId: "b", state: "reconnecting" },
    ]);
    expect(workers).toHaveLength(3); // c started, a not restarted
    expect(workers[2]!.init).toMatchObject({ connectionId: "c" });
    expect(workers[1]!.received.some((m) => m.type === "stop")).toBe(true); // b stopped
    expect(workers[0]!.received.some((m) => m.type === "stop")).toBe(false);
    expect(events.filter((e) => e.event.type === "state" && e.event.state === "stopped").map((e) => e.connectionId)).toEqual([
      "b",
    ]);
    expect(sup.health().accounts.map((a) => a.connectionId).sort()).toEqual(["a", "c"]);
  });

  it("stops everything when the lease is not granted", async () => {
    const { api, sup, workers } = setup();
    api.syncResponses.push(granted(acc("a"), acc("b")));
    await sup.syncOnce();
    sup.startLogin("login-1", "UA", null);
    api.syncResponses.push({ leaseGranted: false, accounts: [] });
    await sup.syncOnce();
    expect(workers.every((w) => w.exited)).toBe(true);
    expect(sup.health().accounts).toEqual([]);
    expect(sup.health().logins).toEqual([]);
  });

  it("restarts an account through the new proxy when proxyUrl changes", async () => {
    const { api, sup, workers } = setup();
    api.syncResponses.push(granted(acc("a", "http://u:p@old:1")));
    await sup.syncOnce();
    api.syncResponses.push(granted(acc("a", "http://u:p@new:2")));
    await sup.syncOnce();
    expect(workers).toHaveLength(2);
    expect(workers[0]!.exited).toBe(true);
    expect(workers[1]!.init).toMatchObject({ connectionId: "a", proxyUrl: "http://u:p@new:2" });
  });

  it("never runs two workers for one nick: a start during a stop waits for the old worker to exit", async () => {
    const { sup, workers } = setup();
    sup.startAccount("a", { session: SESSION, proxyUrl: null }, true);
    workers[0]!.autoExitOnStop = false;
    const stopping = sup.stopAccount("a", { emitStopped: false });
    expect(sup.startAccount("a", { session: SESSION, proxyUrl: "http://u:p@new:1" }, true)).toBe("started");
    await flush();
    expect(workers).toHaveLength(1); // still waiting for the old one
    workers[0]!.exit(0);
    await stopping;
    await flush();
    expect(workers).toHaveLength(2);
    expect(workers[1]!.init).toMatchObject({ connectionId: "a", proxyUrl: "http://u:p@new:1" });
  });

  it("re-auth by QR waits for the old worker and blocks sync from starting a duplicate", async () => {
    const { api, sup, workers } = setup();
    api.loginOutcome = { kind: "ok", connectionId: "a" };
    sup.startAccount("a", { session: SESSION, proxyUrl: null }, true);
    const old = workers[0]!;
    old.autoExitOnStop = false;
    sup.startLogin("L-re", "UA", null);
    const login = workers[1]!;
    login.emit({ type: "loginResult", uid: "u", displayName: "N", avatarUrl: null, session: SESSION });
    await flush(30);
    expect(old.received.some((m) => m.type === "stop")).toBe(true);
    api.syncResponses.push(granted(acc("a")));
    await sup.syncOnce(); // must not spawn a third worker while re-keying
    expect(workers).toHaveLength(2);
    old.exit(0);
    await flush(30);
    expect(workers).toHaveLength(2);
    expect(login.received.at(-1)).toEqual({ type: "loginAccepted", connectionId: "a" });
  });

  describe("lease expiry", () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    it("keeps running through short API outages, stops after the lease TTL", async () => {
      const { api, sup, workers } = setup({ leaseTtlMs: 90_000 });
      api.syncResponses.push(granted(acc("a")));
      await sup.syncOnce();
      vi.advanceTimersByTime(60_000);
      api.syncResponses.push(new Error("down"));
      await sup.syncOnce();
      expect(workers[0]!.exited).toBe(false);
      vi.advanceTimersByTime(31_000);
      api.syncResponses.push(new Error("down"));
      await sup.syncOnce();
      await flush();
      expect(workers[0]!.exited).toBe(true);
    });
  });
});

describe("Supervisor crash restarts", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("restarts a crashed worker after 1, 5, 15, 30, 30 minutes", async () => {
    const { api, sup, workers, events } = setup();
    api.syncResponses.push(granted(acc("a")));
    await sup.syncOnce();
    const expected = [1, 5, 15, 30, 30];
    for (let i = 0; i < expected.length; i++) {
      const w = workers[workers.length - 1]!;
      w.exit(2);
      expect(sup.health().accounts).toEqual([{ connectionId: "a", state: "backoff" }]);
      const before = workers.length;
      vi.advanceTimersByTime(expected[i]! * MIN - 1);
      expect(workers.length).toBe(before);
      vi.advanceTimersByTime(1);
      expect(workers.length).toBe(before + 1);
    }
    const reconnecting = events.filter((e) => e.event.type === "state" && e.event.state === "reconnecting");
    expect(reconnecting).toHaveLength(5);
  });

  it("resets the schedule after 10 minutes connected", async () => {
    const { api, sup, workers } = setup();
    api.syncResponses.push(granted(acc("a")));
    await sup.syncOnce();
    workers[0]!.exit(2);
    vi.advanceTimersByTime(1 * MIN);
    workers[1]!.exit(2);
    vi.advanceTimersByTime(5 * MIN);
    const w = workers[2]!;
    w.emit({ type: "event", event: { type: "state", state: "connected", code: null, detail: null, ts: 0 } });
    vi.advanceTimersByTime(10 * MIN);
    w.exit(2);
    vi.advanceTimersByTime(1 * MIN);
    expect(workers).toHaveLength(4);
  });

  it("does not restart an account that ended in a terminal state, until the API changes its mind", async () => {
    const { api, sup, workers } = setup();
    api.syncResponses.push(granted(acc("a")));
    await sup.syncOnce();
    workers[0]!.emit({
      type: "event",
      event: { type: "state", state: "opened_elsewhere", code: 3000, detail: null, ts: 0 },
    });
    workers[0]!.exit(0);
    vi.advanceTimersByTime(60 * MIN);
    expect(workers).toHaveLength(1);
    expect(sup.isTerminal("a")).toBe(true);

    api.syncResponses.push(granted(acc("a"))); // API has not processed the event yet
    await sup.syncOnce();
    expect(workers).toHaveLength(1);

    api.syncResponses.push(granted()); // API marked it disconnected
    await sup.syncOnce();
    expect(sup.isTerminal("a")).toBe(false);

    api.syncResponses.push(granted(acc("a"))); // owner reconnected
    await sup.syncOnce();
    expect(workers).toHaveLength(2);
  });

  it("explicit /start overrides a terminal mark and a pending backoff", async () => {
    const { api, sup, workers } = setup();
    api.syncResponses.push(granted(acc("a")));
    await sup.syncOnce();
    workers[0]!.exit(2); // crash → backoff 1 min
    expect(sup.startAccount("a", { session: SESSION, proxyUrl: null }, true)).toBe("started");
    expect(workers).toHaveLength(2);
    expect(sup.startAccount("a", { session: SESSION, proxyUrl: null }, true)).toBe("running");
    workers[1]!.emit({ type: "event", event: { type: "state", state: "session_expired", code: null, detail: null, ts: 0 } });
    workers[1]!.exit(0);
    expect(sup.startAccount("a", { session: SESSION, proxyUrl: null }, false)).toBe("skipped");
    expect(sup.startAccount("a", { session: SESSION, proxyUrl: null }, true)).toBe("started");
  });
});

describe("Supervisor QR login", () => {
  it("forwards progress in order, re-keys the worker on login-result 200", async () => {
    const { api, sup, workers } = setup();
    api.loginOutcome = { kind: "ok", connectionId: "conn-9" };
    sup.startLogin("L1", "UA-1", null);
    expect(sup.startLogin("L1", "UA-1", null)).toBe("running");
    const w = workers[0]!;
    expect(w.init).toEqual({ role: "login", loginId: "L1", userAgent: "UA-1", proxyUrl: null, mode: "fake" });
    w.emit({ type: "loginProgress", state: "qr", qrPng: "AAAA" });
    w.emit({ type: "loginProgress", state: "scanned" });
    w.emit({ type: "loginResult", uid: "u1", displayName: "Nick", avatarUrl: null, session: SESSION });
    await flush(30);
    expect(api.progress.map((p) => p.state)).toEqual(["qr", "scanned"]);
    expect(api.progress[0]!.qrPng).toBe("AAAA");
    expect(api.loginResults).toEqual([{ loginId: "L1", uid: "u1", displayName: "Nick" }]);
    expect(w.received.at(-1)).toEqual({ type: "loginAccepted", connectionId: "conn-9" });
    expect(sup.health()).toMatchObject({ logins: [], accounts: [{ connectionId: "conn-9", state: "starting" }] });

    // Events from the re-keyed worker are attributed to the connection.
    w.emit({ type: "ready" });
    w.emit({ type: "event", event: { type: "state", state: "connected", code: null, detail: null, ts: 0 } });
    expect(sup.health().accounts).toEqual([{ connectionId: "conn-9", state: "connected" }]);
  });

  it("stops the login worker on 409", async () => {
    const { api, sup, workers } = setup();
    api.loginOutcome = { kind: "conflict", error: "already_connected_elsewhere" };
    sup.startLogin("L2", "UA", null);
    workers[0]!.emit({ type: "loginResult", uid: "u1", displayName: "N", avatarUrl: null, session: SESSION });
    await flush(30);
    expect(workers[0]!.received.at(-1)).toEqual({ type: "loginRejected" });
    expect(sup.health()).toMatchObject({ logins: [], accounts: [] });
  });

  it("stops the login when the API says the loginId is gone", async () => {
    const { api, sup, workers } = setup();
    api.progressAnswer = "gone";
    sup.startLogin("L3", "UA", null);
    workers[0]!.emit({ type: "loginProgress", state: "qr", qrPng: "AAAA" });
    await flush(30);
    expect(workers[0]!.received.some((m) => m.type === "stop")).toBe(true);
    expect(sup.health().logins).toEqual([]);
  });

  describe("timeout", () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    it("reports expired after 5 minutes and stops the worker", async () => {
      const { api, sup, workers } = setup();
      sup.startLogin("L4", "UA", null);
      await vi.advanceTimersByTimeAsync(5 * MIN);
      expect(api.progress).toEqual([{ loginId: "L4", state: "expired" }]);
      expect(workers[0]!.exited).toBe(true);
      expect(sup.health().logins).toEqual([]);
    });
  });
});
