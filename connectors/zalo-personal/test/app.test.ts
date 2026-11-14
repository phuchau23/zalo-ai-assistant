// App-level test in fake mode: real HTTP server + HMAC both ways + supervisor + in-process workers
// running FakeZaloSession. Covers the CONTRACT §7 flows and asserts nothing sensitive is logged.
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { startApp, type App } from "../src/main.ts";
import { signRequest, verifySignature } from "../src/hmac.ts";
import { clearSecrets, createLogger } from "../src/log.ts";
import { FakeZaloSession } from "../src/session/fake-session.ts";
import type { FetchFn } from "../src/api-client.ts";
import type { MessageEvent, PersonalEvent } from "../src/types.ts";
import { inProcessSpawner } from "./helpers.ts";

const SECRET = "test-shared-secret-0123456789-abcdef";
const PROXY = "http://user:pa55word@proxy.local:8080";
const UA = "Mozilla/5.0 SECRET-UA-STRING";
const TEXT_IN = "Chị cần tư vấn gói chăm sóc da";
const TEXT_OUT = "Dạ em chào chị, bên em có gói 3 buổi";
const PHONE = "0912345678";
const CUSTOMER = "Nguyễn Thị Hoa";

type Recorded = { path: string; body: Record<string, unknown> };

const logLines: string[] = [];
const apiCalls: Recorded[] = [];
let badSignatures = 0;
let app: App;
let connectionCounter = 0;

const mockApi: FetchFn = async (url, init) => {
  const u = new URL(url);
  const body = init.body ?? "";
  const v = verifySignature(SECRET, init.headers["x-remi-signature"], init.method, u.pathname + u.search, body);
  if (!v.ok) badSignatures++;
  const json = body ? (JSON.parse(body) as Record<string, unknown>) : {};
  apiCalls.push({ path: u.pathname, body: json });
  const reply = (status: number, b?: unknown) => ({ status, text: async () => (b === undefined ? "" : JSON.stringify(b)) });
  switch (u.pathname) {
    case "/internal/zalo-personal/sync":
      return reply(200, { leaseGranted: true, accounts: [] });
    case "/internal/zalo-personal/login-result":
      if (json.loginId === "login-conflict") return reply(409, { error: "already_connected_elsewhere" });
      return reply(200, { connectionId: `conn-${++connectionCounter}` });
    default:
      return reply(204);
  }
};

async function call(method: string, path: string, body?: unknown, sign = true): Promise<{ status: number; body: any }> {
  const raw = body === undefined ? "" : JSON.stringify(body);
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (sign) headers["x-remi-signature"] = signRequest(SECRET, method, path, raw);
  const res = await fetch(`http://127.0.0.1:${app.port}${path}`, { method, headers, body: raw || undefined });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
async function waitFor<T>(fn: () => T | undefined | null | false, timeoutMs = 5000): Promise<T> {
  const end = Date.now() + timeoutMs;
  for (;;) {
    const v = fn();
    if (v) return v;
    if (Date.now() > end) throw new Error("timeout waiting for condition");
    await sleep(20);
  }
}

const eventsFor = (connectionId: string): PersonalEvent[] =>
  apiCalls
    .filter((c) => c.path === "/internal/zalo-personal/events" && c.body.connectionId === connectionId)
    .flatMap((c) => c.body.events as PersonalEvent[]);

async function connectNick(loginId: string, uid?: string): Promise<string> {
  expect((await call("POST", "/login-qr", { loginId, userAgent: UA, proxyUrl: PROXY })).status).toBe(202);
  await waitFor(() => apiCalls.find((c) => c.body.loginId === loginId && c.body.state === "qr"));
  expect((await call("POST", "/dev/simulate", { action: "scan_qr", loginId, uid, displayName: CUSTOMER })).status).toBe(204);
  await waitFor(() =>
    apiCalls.find((c) => c.path === "/internal/zalo-personal/login-result" && c.body.loginId === loginId),
  );
  // The mock API hands out conn-1, conn-2... in order (logins run one at a time here).
  const connectionId = `conn-${connectionCounter}`;
  await waitFor(() => eventsFor(connectionId).find((e) => e.type === "state" && e.state === "connected"));
  return connectionId;
}

beforeAll(async () => {
  clearSecrets();
  const log = createLogger({ level: "debug", sink: (l) => logLines.push(l) });
  const { spawn } = inProcessSpawner({
    log,
    createSession: async () =>
      new FakeZaloSession({ rejectFriendMs: 50, acceptFriendMs: 80, echoDelayMs: 20, reconnectMs: 50 }),
  });
  app = await startApp(
    {
      port: 0,
      host: "127.0.0.1",
      apiUrl: "http://api.internal:4001",
      sharedSecret: SECRET,
      mode: "fake",
      instanceId: "test-instance",
      syncIntervalMs: 60_000,
      logLevel: "debug",
    },
    { log, spawn, fetchFn: mockApi },
  );
});

afterAll(async () => {
  await app.close();
});

describe("HTTP + HMAC", () => {
  it("rejects unsigned and wrongly signed requests with 401", async () => {
    expect((await call("GET", "/health", undefined, false)).status).toBe(401);
    const res = await fetch(`http://127.0.0.1:${app.port}/health`, {
      headers: { "x-remi-signature": signRequest("another-secret-another-secret-xx", "GET", "/health", "") },
    });
    expect(res.status).toBe(401);
  });

  it("serves /health", async () => {
    const r = await call("GET", "/health");
    expect(r).toMatchObject({ status: 200, body: { instance: "test-instance", mode: "fake" } });
  });

  it("validates input and returns 404 for unknown routes", async () => {
    expect((await call("POST", "/login-qr", { loginId: "bad id!", userAgent: "x" })).status).toBe(400);
    expect((await call("POST", "/nope", {})).status).toBe(404);
    expect((await call("POST", "/accounts/unknown/send", { messageId: "m", threadId: "t", text: "x", typingMs: 0 })).body).toEqual({
      error: "not_running",
    });
    expect((await call("POST", "/accounts/unknown/stop")).status).toBe(204);
    expect((await call("POST", "/login-qr/nothing/cancel")).status).toBe(204);
  });

  it("signs every call to the API", async () => {
    await waitFor(() => apiCalls.length > 0);
    expect(badSignatures).toBe(0);
  });
});

describe("fake mode flows (CONTRACT §7)", () => {
  let conn = "";

  it("scan_qr → qr, scanned, login-result → listener connected", async () => {
    conn = await connectNick("login-1", "fake-111111");
    const progress = apiCalls.filter((c) => c.body.loginId === "login-1" && c.path.endsWith("login-progress"));
    expect(progress.map((p) => p.body.state)).toEqual(["qr", "scanned"]);
    expect(typeof progress[0]!.body.qrPng).toBe("string");
    expect(String(progress[0]!.body.qrPng).startsWith("iVBOR")).toBe(true);
    const result = apiCalls.find((c) => c.path.endsWith("login-result") && c.body.loginId === "login-1")!;
    expect(result.body).toMatchObject({ uid: "fake-111111", displayName: CUSTOMER });
    expect(result.body.session).toMatchObject({ userAgent: UA });
    const health = await call("GET", "/health");
    expect(health.body.accounts).toEqual([{ connectionId: conn, state: "connected" }]);
  });

  it("incoming → message event; group messages are dropped", async () => {
    await call("POST", "/dev/simulate", { action: "incoming", connectionId: conn, fromUid: "cust-1", fromName: CUSTOMER, text: `${TEXT_IN} ${PHONE}`, isFriend: true });
    await call("POST", "/dev/simulate", { action: "incoming", connectionId: conn, fromUid: "cust-2", text: "in a group", group: true });
    await call("POST", "/dev/simulate", { action: "incoming", connectionId: conn, fromUid: "cust-3", kind: "sticker" });
    const msgs = await waitFor(() => {
      const m = eventsFor(conn).filter((e): e is MessageEvent => e.type === "message");
      return m.length >= 2 ? m : null;
    });
    await sleep(100);
    const all = eventsFor(conn).filter((e): e is MessageEvent => e.type === "message");
    expect(all).toHaveLength(2);
    expect(msgs[0]).toMatchObject({ threadId: "cust-1", fromName: CUSTOMER, isSelf: false, isFriend: true, kind: "text", text: `${TEXT_IN} ${PHONE}` });
    expect(msgs[1]).toMatchObject({ threadId: "cust-3", kind: "sticker", text: null, isFriend: false });
  });

  it("send → 200 {msgId}; retry with same messageId returns the same msgId; echo is not forwarded", async () => {
    const body = { messageId: "msg-uuid-1", threadId: "cust-1", text: TEXT_OUT, typingMs: 30 };
    const a = await call("POST", `/accounts/${conn}/send`, body);
    const b = await call("POST", `/accounts/${conn}/send`, body);
    expect(a.status).toBe(200);
    expect(b.body).toEqual(a.body);
    await sleep(3300); // longer than the 3 s isSelf hold
    const selfMsgs = eventsFor(conn).filter((e) => e.type === "message" && e.isSelf);
    expect(selfMsgs).toEqual([]);
  });

  it("self_message from the owner's phone is forwarded as isSelf after the hold", async () => {
    await call("POST", "/dev/simulate", { action: "self_message", connectionId: conn, toUid: "cust-1", text: "nhắn tay" });
    const ev = await waitFor(() => eventsFor(conn).find((e) => e.type === "message" && e.isSelf), 5000);
    expect(ev).toMatchObject({ isSelf: true, threadId: "cust-1", text: "nhắn tay" });
  });

  it("[[blocked]] → 422 rejected, [[down]] → 503", async () => {
    const r1 = await call("POST", `/accounts/${conn}/send`, { messageId: "m-b", threadId: "c", text: `${TEXT_OUT} [[blocked]]`, typingMs: 0 });
    expect(r1.status).toBe(422);
    expect(r1.body).toMatchObject({ error: "rejected", code: 114 });
    expect(JSON.stringify(r1.body)).not.toContain(TEXT_OUT);
    const r2 = await call("POST", `/accounts/${conn}/send`, { messageId: "m-d", threadId: "c", text: "x [[down]]", typingMs: 0 });
    expect(r2).toEqual({ status: 503, body: { error: "unavailable" } });
  });

  it("find-user and friend requests", async () => {
    expect(await call("POST", `/accounts/${conn}/find-user`, { phone: PHONE })).toEqual({
      status: 200,
      body: { uid: `fake-${PHONE}`, displayName: "Fake 5678", avatarUrl: null },
    });
    expect(await call("POST", `/accounts/${conn}/find-user`, { phone: "0912345000" })).toEqual({
      status: 404,
      body: { error: "not_found" },
    });
    expect((await call("POST", `/accounts/${conn}/friend-request`, { uid: "user-block-1", message: "Chào chị" })).status).toBe(204);
    expect((await call("POST", `/accounts/${conn}/friend-request`, { uid: "user-ok-1", message: "Chào chị" })).status).toBe(204);
    expect((await call("POST", `/accounts/${conn}/undo-friend-request`, { uid: "user-ok-2" })).status).toBe(204);
    const friendEvents = await waitFor(() => {
      const f = eventsFor(conn).filter((e) => e.type === "friend");
      return f.length >= 2 ? f : null;
    });
    expect(friendEvents).toEqual([
      expect.objectContaining({ event: "reject_request", uid: "user-block-1" }),
      expect.objectContaining({ event: "add", uid: "user-ok-1" }),
    ]);
    // isFriend now true for the accepted uid
    await call("POST", "/dev/simulate", { action: "incoming", connectionId: conn, fromUid: "user-ok-1", text: "hi" });
    const m = await waitFor(() => eventsFor(conn).find((e) => e.type === "message" && e.threadId === "user-ok-1"));
    expect(m).toMatchObject({ isFriend: true });
  });

  it("friend simulation and close 1006 (auto reconnect)", async () => {
    await call("POST", "/dev/simulate", { action: "friend", connectionId: conn, uid: "cust-1", event: "remove" });
    await call("POST", "/dev/simulate", { action: "close", connectionId: conn, code: 1006 });
    await waitFor(() => eventsFor(conn).find((e) => e.type === "friend" && e.event === "remove"));
    await waitFor(() => {
      const st = eventsFor(conn).filter((e) => e.type === "state").map((e) => (e.type === "state" ? e.state : ""));
      return st.lastIndexOf("connected") > st.indexOf("reconnecting") && st.includes("reconnecting");
    });
  });

  it("close 3000 → opened_elsewhere, nick stops and is not restarted by sync", async () => {
    await call("POST", "/dev/simulate", { action: "close", connectionId: conn, code: 3000 });
    await waitFor(() => eventsFor(conn).find((e) => e.type === "state" && e.state === "opened_elsewhere"));
    await waitFor(() => !app.supervisor.health().accounts.some((a) => a.connectionId === conn));
    expect(app.supervisor.isTerminal(conn)).toBe(true);
  });

  it("session_expired and proxy_fail simulations stop the nick with that state", async () => {
    const c2 = await connectNick("login-2");
    await call("POST", "/dev/simulate", { action: "session_expired", connectionId: c2 });
    await waitFor(() => eventsFor(c2).find((e) => e.type === "state" && e.state === "session_expired"));
    const c3 = await connectNick("login-3");
    await call("POST", "/dev/simulate", { action: "proxy_fail", connectionId: c3 });
    await waitFor(() => eventsFor(c3).find((e) => e.type === "state" && e.state === "proxy_failing"));
    await waitFor(() => app.supervisor.health().accounts.length === 0);
  });

  it("decline_qr / expire_qr / 409 on login-result", async () => {
    await call("POST", "/login-qr", { loginId: "login-d", userAgent: UA, proxyUrl: null });
    await waitFor(() => apiCalls.find((c) => c.body.loginId === "login-d" && c.body.state === "qr"));
    await call("POST", "/dev/simulate", { action: "decline_qr", loginId: "login-d" });
    await waitFor(() => apiCalls.find((c) => c.body.loginId === "login-d" && c.body.state === "declined"));

    await call("POST", "/login-qr", { loginId: "login-e", userAgent: UA, proxyUrl: null });
    await waitFor(() => apiCalls.find((c) => c.body.loginId === "login-e" && c.body.state === "qr"));
    await call("POST", "/dev/simulate", { action: "expire_qr", loginId: "login-e" });
    await waitFor(() => apiCalls.find((c) => c.body.loginId === "login-e" && c.body.state === "expired"));

    await call("POST", "/login-qr", { loginId: "login-conflict", userAgent: UA, proxyUrl: null });
    await waitFor(() => apiCalls.find((c) => c.body.loginId === "login-conflict" && c.body.state === "qr"));
    await call("POST", "/dev/simulate", { action: "scan_qr", loginId: "login-conflict" });
    await waitFor(() => apiCalls.find((c) => c.path.endsWith("login-result") && c.body.loginId === "login-conflict"));
    await waitFor(() => app.supervisor.health().logins.length === 0);
    expect(app.supervisor.health().accounts).toEqual([]);
  });

  it("/proxies/test is answered without leaking the proxy URL", async () => {
    const r = await call("POST", "/proxies/test", { proxyUrl: PROXY });
    expect(r.body).toMatchObject({ ok: true });
    expect(JSON.stringify(r.body)).not.toContain("pa55word");
  });
});

describe("log redaction", () => {
  it("never logs cookie, imei, userAgent, proxyUrl, message text, phone or customer names", () => {
    const all = logLines.join("\n");
    expect(logLines.length).toBeGreaterThan(20);
    for (const line of logLines) expect(() => JSON.parse(line)).not.toThrow();
    const forbidden = [
      SECRET,
      PROXY,
      "pa55word",
      UA,
      "SECRET-UA",
      "fake-sek-",
      "fake-imei-",
      TEXT_IN,
      TEXT_OUT,
      "nhắn tay",
      PHONE,
      CUSTOMER,
      "Chào chị",
      "iVBOR", // QR image
    ];
    for (const f of forbidden) expect(all, `log contains ${f}`).not.toContain(f);
  });
});
