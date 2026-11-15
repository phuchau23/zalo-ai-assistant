import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { EventQueue, type PostResult } from "../src/event-queue.ts";
import type { PersonalEvent, StateEvent } from "../src/types.ts";
import { captureLogger, flush, silentLog } from "./helpers.ts";

const ev = (n: number): StateEvent => ({ type: "state", state: "connected", code: null, detail: `e${n}`, ts: n });
const ids = (evs: PersonalEvent[]) => evs.map((e) => (e as StateEvent).detail);

describe("EventQueue", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("delivers in order per connection, in batches", async () => {
    const batches: { conn: string; events: PersonalEvent[] }[] = [];
    const q = new EventQueue({
      post: async (conn, events) => {
        batches.push({ conn, events });
        return { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: () => false,
      log: silentLog,
      batchSize: 2,
    });
    for (let i = 1; i <= 5; i++) q.push("a", ev(i));
    q.push("b", ev(100));
    await vi.advanceTimersByTimeAsync(10);
    await flush();
    await vi.advanceTimersByTimeAsync(10);
    const a = batches.filter((b) => b.conn === "a").flatMap((b) => ids(b.events));
    expect(a).toEqual(["e1", "e2", "e3", "e4", "e5"]);
    expect(batches.filter((b) => b.conn === "a").every((b) => b.events.length <= 2)).toBe(true);
    expect(batches.filter((b) => b.conn === "b").flatMap((b) => ids(b.events))).toEqual(["e100"]);
    expect(q.isIdle()).toBe(true);
  });

  it("retries network errors / 5xx with exponential backoff and keeps order", async () => {
    const results: PostResult[] = [{ kind: "retry", status: 503 }, { kind: "retry" }, { kind: "ok" }, { kind: "ok" }];
    const calls: { at: number; events: string[] }[] = [];
    const q = new EventQueue({
      post: async (_c, events) => {
        calls.push({ at: Date.now(), events: ids(events) as string[] });
        return results.shift() ?? { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: () => false,
      log: silentLog,
      retryBaseMs: 1000,
    });
    q.push("a", ev(1));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toHaveLength(1);
    q.push("a", ev(2)); // arrives during backoff: must not cut the wait short
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toHaveLength(3);
    expect(calls[1]!.at - calls[0]!.at).toBe(1000);
    expect(calls[2]!.at - calls[1]!.at).toBe(2000);
    expect(calls[1]!.events).toEqual(["e1", "e2"]);
    expect(calls[2]!.events).toEqual(["e1", "e2"]);
    expect(q.isIdle()).toBe(true);
  });

  it("drops a batch on other 4xx and continues", async () => {
    const { log, lines } = captureLogger();
    const results: PostResult[] = [{ kind: "drop", status: 400 }, { kind: "ok" }];
    const seen: string[][] = [];
    const q = new EventQueue({
      post: async (_c, events) => {
        seen.push(ids(events) as string[]);
        return results.shift() ?? { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: () => false,
      log,
      batchSize: 1,
    });
    q.push("a", ev(1));
    q.push("a", ev(2));
    await vi.advanceTimersByTimeAsync(10);
    await flush();
    await vi.advanceTimersByTimeAsync(10);
    expect(seen).toEqual([["e1"], ["e2"]]);
    expect(lines.some((l) => l.includes("API rejected events") && l.includes('"status":400'))).toBe(true);
  });

  it("on 404 drops the connection's events and reports it gone", async () => {
    const gone: string[] = [];
    const post = vi.fn(async (): Promise<PostResult> => ({ kind: "gone" }));
    const q = new EventQueue({ post, onGone: (c) => gone.push(c), isSentByUs: () => false, log: silentLog, batchSize: 1 });
    q.push("a", ev(1));
    q.push("a", ev(2));
    q.push("a", ev(3));
    await vi.advanceTimersByTimeAsync(100);
    expect(gone).toEqual(["a"]);
    expect(post).toHaveBeenCalledTimes(1);
    expect(q.size()).toBe(0);
  });

  it("is bounded: drops the oldest event with an error log when full", async () => {
    const { log, lines } = captureLogger();
    const posted: string[] = [];
    let fail = true;
    const q = new EventQueue({
      post: async (_c, events) => {
        if (fail) return { kind: "retry" };
        posted.push(...(ids(events) as string[]));
        return { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: () => false,
      log,
      maxSize: 3,
      batchSize: 10,
      retryBaseMs: 1000,
    });
    q.push("a", ev(1));
    await vi.advanceTimersByTimeAsync(1); // e1 in flight → fails → back in queue
    q.push("b", ev(2));
    q.push("a", ev(3));
    q.push("b", ev(4)); // 4 > 3 → drop oldest (e1)
    expect(q.size()).toBe(3);
    expect(lines.filter((l) => l.includes("event queue full"))).toHaveLength(1);
    fail = false;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(posted.sort()).toEqual(["e2", "e3", "e4"]);
  });

  it("holds an isSelf message at the head so later events keep their order", async () => {
    const posted: string[] = [];
    const q = new EventQueue({
      post: async (_c, events) => {
        posted.push(...events.map((e) => (e.type === "message" ? `msg:${e.msgId}` : `state:${(e as StateEvent).detail}`)));
        return { kind: "ok" };
      },
      onGone: () => {},
      isSentByUs: () => false,
      log: silentLog,
    });
    q.push("a", {
      type: "message",
      msgId: "s1",
      threadId: "c",
      fromUid: "o",
      fromName: null,
      isSelf: true,
      isFriend: false,
      kind: "text",
      text: "x",
      ts: 1,
    });
    q.push("a", ev(9));
    await vi.advanceTimersByTimeAsync(1000);
    expect(posted).toEqual([]);
    await vi.advanceTimersByTimeAsync(2100);
    expect(posted).toEqual(["msg:s1", "state:e9"]);
  });
});
