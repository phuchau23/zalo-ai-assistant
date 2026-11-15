import { describe, expect, it, vi } from "vitest";
import { ZaloApiError, ZaloApiLoginQRAborted } from "zca-js";
import { classifyError, errorToResult, isNetworkError } from "../src/errors.ts";
import { MAX_TYPING_MS, WorkerCore } from "../src/worker.ts";
import { isSessionRefused } from "../src/session/real-session.ts";
import {
  SessionExpiredError,
  sessionFingerprint,
  type ListenerHandlers,
  type LoginOutcome,
  type QrHandlers,
  type ZaloSession,
} from "../src/session/zalo-session.ts";
import { FriendEventType } from "../src/translate.ts";
import { EXIT_CRASH, EXIT_OK, type WorkerInit, type WorkerToParent, type ZaloSessionData } from "../src/types.ts";
import { SESSION, flush, silentLog } from "./helpers.ts";

describe("send error mapping (CONTRACT §5)", () => {
  it("Zalo error with code → 422 rejected with code and a reason without the text", () => {
    const text = "Chào chị Lan, số em là 0912345678";
    const err = new ZaloApiError(`Không gửi được: ${text}`, 114);
    expect(err.name).toBe("ZcaApiError"); // zca-js 2.2.0 naming
    const r = errorToResult(err, text);
    expect(r.status).toBe(422);
    const body = r.body as { error: string; code: number; reason: string };
    expect(body.error).toBe("rejected");
    expect(body.code).toBe(114);
    expect(body.reason).not.toContain("Chào chị Lan");
    expect(body.reason).not.toContain("0912345678");
    expect(body.reason.length).toBeLessThanOrEqual(200);
  });

  it("long reasons are truncated to 200 chars", () => {
    const r = errorToResult(new ZaloApiError("x".repeat(500), -1));
    expect((r.body as { reason: string }).reason).toHaveLength(200);
  });

  it("HTTP failure inside zca-js → 503", () => {
    expect(errorToResult(new ZaloApiError("Request failed with status code 502"))).toEqual({
      status: 503,
      body: { error: "unavailable" },
    });
  });

  it("network, timeout and proxy errors → 503", () => {
    const fetchFailed = new TypeError("fetch failed");
    (fetchFailed as { cause?: unknown }).cause = { code: "ECONNREFUSED" };
    const nodeFetchErr = Object.assign(new Error("request to x failed"), { name: "FetchError", code: "ECONNRESET" });
    const abort = Object.assign(new Error("aborted"), { name: "AbortError" });
    const proxy = Object.assign(new Error("tunnel"), { code: "ERR_PROXY_TUNNEL" });
    for (const e of [fetchFailed, nodeFetchErr, abort, proxy]) {
      expect(errorToResult(e).status).toBe(503);
      expect(isNetworkError(e)).toBe(true);
    }
  });

  it("zca-js errors without code: input problems → 422, others → 503", () => {
    expect(classifyError(new ZaloApiError("Missing threadId"))).toMatchObject({ kind: "rejected", code: null });
    expect(classifyError(new ZaloApiError("Failed to parse response data"))).toEqual({ kind: "unavailable" });
    expect(classifyError(new Error("weird"))).toEqual({ kind: "unavailable" });
  });

  it("login errors: refused session vs network", () => {
    expect(isSessionRefused(new ZaloApiError("Đăng nhập thất bại"))).toBe(true);
    expect(isSessionRefused(new ZaloApiError("Failed to fetch login info: Bad Gateway"))).toBe(false);
    expect(isSessionRefused(new TypeError("fetch failed"))).toBe(false);
    expect(new ZaloApiLoginQRAborted().name).toBe("ZaloApiLoginQRAborted");
  });
});

// ---------------------------------------------------------------------------

class StubSession implements ZaloSession {
  handlers: ListenerHandlers | null = null;
  loginError: unknown = null;
  sendError: unknown = null;
  friends: string[] = ["friend-1"];
  calls: string[] = [];
  session: ZaloSessionData = SESSION;
  async loginQR(_userAgent: string, _handlers: QrHandlers): Promise<LoginOutcome> {
    throw new Error("not used");
  }
  async loginWithSession(): Promise<void> {
    this.calls.push("login");
    if (this.loginError) throw this.loginError;
  }
  ownUid() {
    return "own-1";
  }
  startListener(h: ListenerHandlers) {
    this.handlers = h;
    this.calls.push("listen");
  }
  async listFriendIds() {
    return this.friends;
  }
  async sendTyping(threadId: string) {
    this.calls.push(`typing:${threadId}`);
  }
  async sendText(threadId: string) {
    this.calls.push(`send:${threadId}`);
    if (this.sendError) throw this.sendError;
    return "8888";
  }
  async findUser(phone: string) {
    return phone.endsWith("000") ? null : { uid: "u-" + phone, displayName: "X", avatarUrl: null };
  }
  async sendFriendRequest() {}
  async undoFriendRequest() {}
  currentSession() {
    return this.session;
  }
  stop() {
    this.calls.push("stop");
  }
}

function makeWorker(session: StubSession, opts: { proxyOk?: boolean } = {}) {
  const out: WorkerToParent[] = [];
  const exits: number[] = [];
  const sleeps: number[] = [];
  const core = new WorkerCore({
    io: { send: (m) => out.push(m), exit: (c) => exits.push(c) },
    log: silentLog,
    createSession: async () => session,
    proxyTest: async () => (opts.proxyOk === false ? { ok: false, error: "ECONNREFUSED" } : { ok: true }),
    sleep: async (ms) => {
      sleeps.push(ms);
    },
    exitDelayMs: 0,
    sessionCheckIntervalMs: 1_000_000_000,
  });
  return { core, out, exits, sleeps };
}

const accountInit = (proxyUrl: string | null = null): WorkerInit => ({
  role: "account",
  connectionId: "conn-1",
  session: SESSION,
  proxyUrl,
  mode: "real",
});

async function settle() {
  await flush(20);
  await new Promise((r) => setTimeout(r, 5));
}

const states = (out: WorkerToParent[]) =>
  out.flatMap((m) => (m.type === "event" && m.event.type === "state" ? [m.event.state] : []));

describe("WorkerCore account lifecycle", () => {
  it("logs in, reports ready, loads friends and starts the listener", async () => {
    const s = new StubSession();
    const { core, out } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    expect(s.calls).toEqual(["login", "listen"]);
    expect(out[0]).toEqual({ type: "ready" });
    s.handlers!.onConnected();
    s.handlers!.onMessage({
      type: 0,
      threadId: "friend-1",
      isSelf: false,
      data: { msgId: 1, msgType: "webchat", uidFrom: "friend-1", ts: "5", content: "hi" },
    });
    s.handlers!.onMessage({
      type: 1,
      threadId: "group",
      isSelf: false,
      data: { msgId: 2, msgType: "webchat", uidFrom: "x", ts: "5", content: "group msg" },
    });
    s.handlers!.onFriendEvent({ type: FriendEventType.ADD, threadId: "new-1", isSelf: false, data: "new-1" });
    s.handlers!.onMessage({
      type: 0,
      threadId: "new-1",
      isSelf: false,
      data: { msgId: 3, msgType: "webchat", uidFrom: "new-1", ts: "6", content: "hey" },
    });
    const events = out.flatMap((m) => (m.type === "event" ? [m.event] : []));
    expect(events.map((e) => e.type)).toEqual(["state", "message", "friend", "message"]);
    expect(events[1]).toMatchObject({ msgId: "1", isFriend: true, text: "hi" });
    expect(events[3]).toMatchObject({ msgId: "3", isFriend: true });
  });

  it("stored session refused → session_expired, exit OK", async () => {
    const s = new StubSession();
    s.loginError = new SessionExpiredError();
    const { core, out, exits } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    expect(states(out)).toEqual(["session_expired"]);
    expect(exits).toEqual([EXIT_OK]);
  });

  it("network error at login through a dead proxy → proxy_failing (fail closed)", async () => {
    const s = new StubSession();
    s.loginError = new TypeError("fetch failed");
    const { core, out, exits } = makeWorker(s, { proxyOk: false });
    core.handle({ type: "init", init: accountInit("http://u:p@proxy:1") });
    await settle();
    expect(states(out)).toEqual(["proxy_failing"]);
    expect(exits).toEqual([EXIT_OK]);
  });

  it("network error at login without proxy → reconnecting + crash exit (supervisor backoff)", async () => {
    const s = new StubSession();
    s.loginError = new TypeError("fetch failed");
    const { core, out, exits } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    expect(states(out)).toEqual(["reconnecting"]);
    expect(exits).toEqual([EXIT_CRASH]);
  });

  it("close 3000/3003 → opened_elsewhere, no restart", async () => {
    const s = new StubSession();
    const { core, out, exits } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    s.handlers!.onDisconnected(3003, "");
    await settle();
    expect(states(out)).toEqual(["opened_elsewhere"]);
    expect(exits).toEqual([EXIT_OK]);
  });

  it("1006 → reconnecting; listener gives up → crash exit", async () => {
    const s = new StubSession();
    const { core, out, exits } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    s.handlers!.onDisconnected(1006, "");
    s.handlers!.onClosed(1006, "");
    await settle();
    expect(states(out)).toEqual(["reconnecting", "reconnecting"]);
    expect(exits).toEqual([EXIT_CRASH]);
  });

  it("listener gives up and the proxy is dead → proxy_failing", async () => {
    const s = new StubSession();
    const { core, out, exits } = makeWorker(s, { proxyOk: false });
    core.handle({ type: "init", init: accountInit("http://u:p@proxy:1") });
    await settle();
    s.handlers!.onClosed(1006, "");
    await settle();
    expect(states(out)).toEqual(["proxy_failing"]);
    expect(exits).toEqual([EXIT_OK]);
  });

  it("6-hourly check posts the session only when the cookie changed", async () => {
    const s = new StubSession();
    const { core, out } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    // Only lastAccessed changed → same fingerprint.
    s.session = {
      ...SESSION,
      cookie: [{ key: "zpw_sek", value: "c00kie-SECRET-value", lastAccessed: "2026-01-01" }],
    };
    expect(sessionFingerprint(s.session)).toBe(sessionFingerprint(SESSION));
    await core.periodicCheck();
    expect(out.filter((m) => m.type === "session")).toHaveLength(0);
    s.session = { ...SESSION, cookie: [{ key: "zpw_sek", value: "refreshed" }] };
    await core.periodicCheck();
    expect(out.filter((m) => m.type === "session")).toEqual([{ type: "session", session: s.session }]);
  });

  it("pushes the cookie Zalo rotated during login as soon as the account is live, and again after a reconnect", async () => {
    // Real-nick test 2026-11-13: only the pre-login cookie was stored → the next restart was refused (session_expired).
    const s = new StubSession();
    s.session = { ...SESSION, cookie: [{ key: "zpw_sek", value: "rotated-at-login" }] };
    const { core, out } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    expect(out.filter((m) => m.type === "session")).toEqual([{ type: "session", session: s.session }]);

    s.handlers!.onConnected();
    expect(out.filter((m) => m.type === "session")).toHaveLength(1); // unchanged → not sent again
    s.session = { ...SESSION, cookie: [{ key: "zpw_sek", value: "rotated-on-reconnect" }] };
    s.handlers!.onConnected();
    expect(out.filter((m) => m.type === "session").at(-1)).toEqual({ type: "session", session: s.session });
  });
});

describe("WorkerCore send", () => {
  it("typing → wait min(typingMs, 15 s) → send → 200 {msgId}", async () => {
    const s = new StubSession();
    const { core, out, sleeps } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    core.handle({ type: "cmd", id: 1, command: { cmd: "send", args: { threadId: "u1", text: "hi", typingMs: 60_000 } } });
    await settle();
    expect(sleeps).toEqual([MAX_TYPING_MS]);
    expect(s.calls.slice(-2)).toEqual(["typing:u1", "send:u1"]);
    expect(out.at(-1)).toEqual({ type: "reply", id: 1, result: { status: 200, body: { msgId: "8888" } } });
  });

  it("typingMs 0 skips typing", async () => {
    const s = new StubSession();
    const { core, sleeps } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    core.handle({ type: "cmd", id: 2, command: { cmd: "send", args: { threadId: "u1", text: "hi", typingMs: 0 } } });
    await settle();
    expect(sleeps).toEqual([]);
    expect(s.calls.at(-1)).toBe("send:u1");
  });

  it("maps a Zalo rejection to 422 and network to 503", async () => {
    const s = new StubSession();
    const { core, out } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    s.sendError = new ZaloApiError("Người nhận chặn tin từ người lạ", -201);
    core.handle({ type: "cmd", id: 3, command: { cmd: "send", args: { threadId: "u1", text: "hi", typingMs: 0 } } });
    await settle();
    expect(out.at(-1)).toMatchObject({ id: 3, result: { status: 422, body: { error: "rejected", code: -201 } } });
    s.sendError = Object.assign(new Error("x"), { name: "FetchError", code: "ETIMEDOUT" });
    core.handle({ type: "cmd", id: 4, command: { cmd: "send", args: { threadId: "u1", text: "hi", typingMs: 0 } } });
    await settle();
    expect(out.at(-1)).toEqual({ type: "reply", id: 4, result: { status: 503, body: { error: "unavailable" } } });
  });

  it("replyTo: customer wrote again during typing → 409 superseded, nothing sent", async () => {
    const s = new StubSession();
    const { core, out } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    const incoming = (msgId: number, isSelf = false) =>
      s.handlers!.onMessage({
        type: 0,
        threadId: "cust-1",
        isSelf,
        data: { msgId, msgType: "webchat", uidFrom: isSelf ? "own-1" : "cust-1", ts: "5", content: "x" },
      });
    incoming(100);

    // Answering the latest message → sent. Our own (isSelf) messages do not count as "newer".
    incoming(101, true);
    core.handle({ type: "cmd", id: 20, command: { cmd: "send", args: { threadId: "cust-1", text: "a", typingMs: 0, replyTo: "100" } } });
    await settle();
    expect(out.at(-1)).toEqual({ type: "reply", id: 20, result: { status: 200, body: { msgId: "8888" } } });

    // A newer customer message arrives while "typing" → dropped after the typing delay.
    const sends = s.calls.filter((c) => c.startsWith("send:")).length;
    core.handle({ type: "cmd", id: 21, command: { cmd: "send", args: { threadId: "cust-1", text: "b", typingMs: 5_000, replyTo: "100" } } });
    incoming(102);
    await settle();
    expect(out.at(-1)).toEqual({ type: "reply", id: 21, result: { status: 409, body: { error: "superseded" } } });
    expect(s.calls.filter((c) => c.startsWith("send:"))).toHaveLength(sends);

    // Already stale before typing starts → no typing event either. No replyTo / unknown thread → always sent.
    const typings = s.calls.filter((c) => c.startsWith("typing:")).length;
    core.handle({ type: "cmd", id: 22, command: { cmd: "send", args: { threadId: "cust-1", text: "c", typingMs: 5_000, replyTo: "100" } } });
    await settle();
    expect(out.at(-1)).toMatchObject({ id: 22, result: { status: 409 } });
    expect(s.calls.filter((c) => c.startsWith("typing:"))).toHaveLength(typings);
    core.handle({ type: "cmd", id: 23, command: { cmd: "send", args: { threadId: "cust-1", text: "d", typingMs: 0 } } });
    core.handle({ type: "cmd", id: 24, command: { cmd: "send", args: { threadId: "other", text: "e", typingMs: 0, replyTo: "1" } } });
    await settle();
    expect(out.filter((m) => m.type === "reply" && (m.id === 23 || m.id === 24)).map((m) => (m as { result: { status: number } }).result.status)).toEqual([200, 200]);
  });

  it("not logged in yet → 409 not_running", async () => {
    const s = new StubSession();
    const { core, out } = makeWorker(s);
    core.handle({ type: "cmd", id: 5, command: { cmd: "send", args: { threadId: "u1", text: "hi", typingMs: 0 } } });
    await settle();
    expect(out.at(-1)).toEqual({ type: "reply", id: 5, result: { status: 409, body: { error: "not_running" } } });
  });

  it("find-user: found → 200, missing → 404", async () => {
    const s = new StubSession();
    const { core, out } = makeWorker(s);
    core.handle({ type: "init", init: accountInit() });
    await settle();
    core.handle({ type: "cmd", id: 6, command: { cmd: "findUser", args: { phone: "0912345678" } } });
    core.handle({ type: "cmd", id: 7, command: { cmd: "findUser", args: { phone: "0912345000" } } });
    await settle();
    const replies = out.filter((m) => m.type === "reply");
    expect(replies).toEqual([
      { type: "reply", id: 6, result: { status: 200, body: { uid: "u-0912345678", displayName: "X", avatarUrl: null } } },
      { type: "reply", id: 7, result: { status: 404, body: { error: "not_found" } } },
    ]);
  });
});

describe("QR expiry decision", () => {
  it("asks for a new QR while enough of the 5-minute window is left", async () => {
    let now = 0;
    let decisions: boolean[] = [];
    const session = new StubSession();
    session.loginQR = (async (_ua: string, h: { onQr(p: string): void; onExpired(): boolean }) => {
      h.onQr("PNG");
      now = 100_000;
      decisions.push(h.onExpired());
      now = 290_000;
      decisions.push(h.onExpired());
      throw new (await import("../src/session/zalo-session.ts")).QrExpiredError();
    }) as unknown as ZaloSession["loginQR"];
    const out: WorkerToParent[] = [];
    const core = new WorkerCore({
      io: { send: (m) => out.push(m), exit: vi.fn() },
      log: silentLog,
      createSession: async () => session,
      now: () => now,
      exitDelayMs: 0,
    });
    core.handle({ type: "init", init: { role: "login", loginId: "L", userAgent: "UA", proxyUrl: null, mode: "real" } });
    await settle();
    expect(decisions).toEqual([true, false]);
    expect(out).toEqual([
      { type: "loginProgress", state: "qr", qrPng: "PNG" },
      { type: "loginProgress", state: "expired" },
    ]);
    decisions = [];
  });
});
