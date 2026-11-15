import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { IDEMPOTENCY_TTL_MS, SentCache } from "../src/sent-cache.ts";
import { EventQueue, type PostResult } from "../src/event-queue.ts";
import { Supervisor } from "../src/supervisor.ts";
import { MockApi, silentLog, stubSpawner, SESSION, flush } from "./helpers.ts";
import type { MessageEvent, PersonalEvent } from "../src/types.ts";

describe("SentCache", () => {
  it("returns the remembered msgId within 1 hour and forgets it after", () => {
    let t = 1_000;
    const c = new SentCache(() => t);
    c.remember("conn", "m1", "555");
    expect(c.get("conn", "m1")).toBe("555");
    expect(c.get("other", "m1")).toBeUndefined();
    t += IDEMPOTENCY_TTL_MS - 1;
    expect(c.get("conn", "m1")).toBe("555");
    t += 2;
    expect(c.get("conn", "m1")).toBeUndefined();
  });

  it("shares one in-flight execution between concurrent duplicates", async () => {
    const c = new SentCache();
    let calls = 0;
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const work = async () => {
      calls++;
      await gate;
      return "msg-1";
    };
    const a = c.once("conn", "m1", work);
    const b = c.once("conn", "m1", work);
    release();
    expect(await a).toBe("msg-1");
    expect(await b).toBe("msg-1");
    expect(calls).toBe(1);
  });

  it("tracks sent msgIds per connection for echo suppression", () => {
    const c = new SentCache();
    c.markSent("conn", "777");
    expect(c.wasSentByUs("conn", "777")).toBe(true);
    expect(c.wasSentByUs("conn2", "777")).toBe(false);
  });
});

describe("Supervisor.send idempotency", () => {
  it("sends once per messageId and replays the old msgId", async () => {
    const { spawn, workers } = stubSpawner();
    const sentCache = new SentCache();
    const sup = new Supervisor({
      instanceId: "i",
      mode: "fake",
      api: new MockApi(),
      spawn,
      pushEvent: () => {},
      sentCache,
      log: silentLog,
    });
    sup.startAccount("conn", { session: SESSION, proxyUrl: null }, true);
    const w = workers[0]!;
    w.emit({ type: "ready" });
    // Worker answers every send with msgId 900 + n.
    let n = 0;
    const origSend = w.send.bind(w);
    w.send = (msg) => {
      origSend(msg);
      if (msg.type === "cmd" && msg.command.cmd === "send") {
        const id = msg.id;
        queueMicrotask(() => w.emit({ type: "reply", id, result: { status: 200, body: { msgId: String(900 + ++n) } } }));
      }
    };
    const args = { threadId: "u1", text: "hi", typingMs: 0 };
    const [a, b] = await Promise.all([sup.send("conn", "m1", args), sup.send("conn", "m1", args)]);
    const c = await sup.send("conn", "m1", args);
    expect(a).toEqual({ status: 200, body: { msgId: "901" } });
    expect(b).toEqual(a);
    expect(c).toEqual(a);
    expect(w.received.filter((m) => m.type === "cmd")).toHaveLength(1);
    expect(sentCache.wasSentByUs("conn", "901")).toBe(true);
    const d = await sup.send("conn", "m2", args);
    expect(d).toEqual({ status: 200, body: { msgId: "902" } });
  });

  it("answers 409 not_running when the nick is not ready", async () => {
    const { spawn } = stubSpawner();
    const sup = new Supervisor({
      instanceId: "i",
      mode: "fake",
      api: new MockApi(),
      spawn,
      pushEvent: () => {},
      sentCache: new SentCache(),
      log: silentLog,
    });
    expect(await sup.send("nope", "m1", { threadId: "u", text: "x", typingMs: 0 })).toEqual({
      status: 409,
      body: { error: "not_running" },
    });
    sup.startAccount("conn", { session: SESSION, proxyUrl: null }, true);
    expect((await sup.send("conn", "m1", { threadId: "u", text: "x", typingMs: 0 })).status).toBe(409);
  });
});

describe("echo suppression timing (isSelf held 3 s)", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const selfMsg = (msgId: string): MessageEvent => ({
    type: "message",
    msgId,
    threadId: "cust",
    fromUid: "own",
    fromName: null,
    isSelf: true,
    isFriend: false,
    kind: "text",
    text: "x",
    ts: 1,
  });

  it("drops an isSelf message whose msgId is recorded as sent within the hold window", async () => {
    const cache = new SentCache();
    const posted: PersonalEvent[] = [];
    const q = new EventQueue({
      post: async (_c, evs): Promise<PostResult> => {
        posted.push(...evs);
        return { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: (c, id) => cache.wasSentByUs(c, id),
      log: silentLog,
    });
    // Echo arrives BEFORE the send call returned its msgId.
    q.push("conn", selfMsg("111"));
    await vi.advanceTimersByTimeAsync(1000);
    cache.remember("conn", "m1", "111");
    await vi.advanceTimersByTimeAsync(2500);
    expect(posted).toEqual([]);
  });

  it("forwards an isSelf message from another device after 3 s", async () => {
    const cache = new SentCache();
    const posted: PersonalEvent[] = [];
    const q = new EventQueue({
      post: async (_c, evs): Promise<PostResult> => {
        posted.push(...evs);
        return { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: (c, id) => cache.wasSentByUs(c, id),
      log: silentLog,
    });
    q.push("conn", selfMsg("222"));
    await vi.advanceTimersByTimeAsync(2999);
    expect(posted).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(2);
    await flush();
    expect(posted.map((e) => (e as MessageEvent).msgId)).toEqual(["222"]);
  });

  it("drops immediately when the msgId is already known as sent", async () => {
    const cache = new SentCache();
    cache.markSent("conn", "333");
    const post = vi.fn(async (): Promise<PostResult> => ({ kind: "ok" }));
    const q = new EventQueue({ post, onGone: () => {}, isSentByUs: (c, id) => cache.wasSentByUs(c, id), log: silentLog });
    q.push("conn", selfMsg("333"));
    expect(q.size()).toBe(0);
    await vi.advanceTimersByTimeAsync(5000);
    expect(post).not.toHaveBeenCalled();
  });
});
