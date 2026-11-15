// In-memory event queue towards POST /internal/zalo-personal/events (CONTRACT §4).
// - Order is kept per connection: one POST in flight per connection, failed batches go back to the head.
// - Network error / 5xx → retry with exponential backoff. 404 → connection is gone (drop + stop nick).
//   Other 4xx → drop that batch, log the status.
// - Bounded (default 5000 events in total); when full the oldest queued event is dropped with an error log.
// - isSelf messages are held for 3 s (head-of-line, so order is kept) and dropped if their msgId turns
//   out to be one the sidecar just sent (echo of our own send).
import type { Logger } from "./log.ts";
import type { PersonalEvent } from "./types.ts";

export type PostResult =
  | { kind: "ok" }
  | { kind: "gone" }
  | { kind: "retry"; status?: number }
  | { kind: "drop"; status: number };

export type EventQueueOptions = {
  post: (connectionId: string, events: PersonalEvent[]) => Promise<PostResult>;
  onGone: (connectionId: string) => void;
  isSentByUs: (connectionId: string, msgId: string) => boolean;
  log: Logger;
  maxSize?: number;
  batchSize?: number;
  selfHoldMs?: number;
  retryBaseMs?: number;
  retryMaxMs?: number;
  now?: () => number;
};

type Item = {
  seq: number;
  event: PersonalEvent;
  releaseAt: number;
  selfMsgId: string | null;
};

export class EventQueue {
  private readonly queues = new Map<string, Item[]>();
  private readonly busy = new Set<string>();
  private readonly attempts = new Map<string, number>();
  private readonly timers = new Map<string, ReturnType<typeof setTimeout>>();
  /** While a connection is backing off, new events must not cut the wait short. */
  private readonly backoffUntil = new Map<string, number>();
  private total = 0;
  private seq = 0;
  private stopped = false;
  private readonly opts: Required<Omit<EventQueueOptions, "now">> & { now: () => number };

  constructor(options: EventQueueOptions) {
    this.opts = {
      maxSize: 5000,
      batchSize: 50,
      selfHoldMs: 3000,
      retryBaseMs: 1000,
      retryMaxMs: 60_000,
      now: () => Date.now(),
      ...options,
    } as Required<Omit<EventQueueOptions, "now">> & { now: () => number };
  }

  /** Number of events waiting (not counting a batch currently being posted). */
  size(): number {
    return this.total;
  }

  isIdle(): boolean {
    return this.total === 0 && this.busy.size === 0;
  }

  push(connectionId: string, event: PersonalEvent): void {
    if (this.stopped) return;
    let selfMsgId: string | null = null;
    let releaseAt = this.opts.now();
    if (event.type === "message" && event.isSelf) {
      if (this.opts.isSentByUs(connectionId, event.msgId)) {
        this.opts.log.debug("dropped echo of own send", { connectionId });
        return;
      }
      selfMsgId = event.msgId;
      releaseAt += this.opts.selfHoldMs;
    }
    let q = this.queues.get(connectionId);
    if (!q) {
      q = [];
      this.queues.set(connectionId, q);
    }
    q.push({ seq: this.seq++, event, releaseAt, selfMsgId });
    this.total++;
    this.trim();
    this.schedule(connectionId, 0);
  }

  /** Drop everything queued for a connection (e.g. after 404). */
  discard(connectionId: string): number {
    const q = this.queues.get(connectionId);
    const n = q ? q.length : 0;
    this.total -= n;
    this.queues.delete(connectionId);
    this.attempts.delete(connectionId);
    this.backoffUntil.delete(connectionId);
    const t = this.timers.get(connectionId);
    if (t) clearTimeout(t);
    this.timers.delete(connectionId);
    return n;
  }

  stop(): void {
    this.stopped = true;
    for (const t of this.timers.values()) clearTimeout(t);
    this.timers.clear();
  }

  private trim(): void {
    while (this.total > this.opts.maxSize) {
      let oldestConn: string | null = null;
      let oldestSeq = Number.POSITIVE_INFINITY;
      for (const [conn, q] of this.queues) {
        const head = q[0];
        if (head && head.seq < oldestSeq) {
          oldestSeq = head.seq;
          oldestConn = conn;
        }
      }
      if (oldestConn === null) return;
      const q = this.queues.get(oldestConn)!;
      const dropped = q.shift()!;
      this.total--;
      this.opts.log.error("event queue full, dropped oldest event", {
        connectionId: oldestConn,
        eventType: dropped.event.type,
        dropped: 1,
      });
    }
  }

  private schedule(connectionId: string, delayMs: number): void {
    if (this.stopped || this.busy.has(connectionId)) return;
    const until = this.backoffUntil.get(connectionId);
    if (until !== undefined) delayMs = Math.max(delayMs, until - this.opts.now());
    const existing = this.timers.get(connectionId);
    if (existing) clearTimeout(existing);
    const timer = setTimeout(() => {
      this.timers.delete(connectionId);
      void this.pump(connectionId);
    }, Math.max(0, delayMs));
    this.timers.set(connectionId, timer);
  }

  private async pump(connectionId: string): Promise<void> {
    if (this.stopped || this.busy.has(connectionId)) return;
    const q = this.queues.get(connectionId);
    if (!q || q.length === 0) {
      this.queues.delete(connectionId);
      return;
    }
    const now = this.opts.now();
    const batch: Item[] = [];
    while (q.length > 0 && batch.length < this.opts.batchSize) {
      const head = q[0]!;
      if (head.releaseAt > now) break;
      q.shift();
      this.total--;
      if (head.selfMsgId !== null && this.opts.isSentByUs(connectionId, head.selfMsgId)) {
        this.opts.log.debug("dropped echo of own send", { connectionId });
        continue;
      }
      batch.push(head);
    }
    if (batch.length === 0) {
      if (q.length > 0) this.schedule(connectionId, q[0]!.releaseAt - now);
      return;
    }

    this.busy.add(connectionId);
    let result: PostResult;
    try {
      result = await this.opts.post(
        connectionId,
        batch.map((i) => i.event),
      );
    } catch {
      result = { kind: "retry" };
    }
    this.busy.delete(connectionId);
    if (this.stopped) return;

    switch (result.kind) {
      case "ok":
        this.attempts.delete(connectionId);
        this.backoffUntil.delete(connectionId);
        break;
      case "gone": {
        const dropped = this.discard(connectionId);
        this.opts.log.warn("connection gone at API, dropping its events", {
          connectionId,
          status: 404,
          dropped: dropped + batch.length,
        });
        this.opts.onGone(connectionId);
        return;
      }
      case "drop":
        this.attempts.delete(connectionId);
        this.backoffUntil.delete(connectionId);
        this.opts.log.error("API rejected events, dropped batch", {
          connectionId,
          status: result.status,
          dropped: batch.length,
        });
        break;
      case "retry": {
        const cur = this.queues.get(connectionId) ?? [];
        cur.unshift(...batch);
        this.queues.set(connectionId, cur);
        this.total += batch.length;
        this.trim();
        const attempt = (this.attempts.get(connectionId) ?? 0) + 1;
        this.attempts.set(connectionId, attempt);
        const delay = Math.min(this.opts.retryBaseMs * 2 ** (attempt - 1), this.opts.retryMaxMs);
        this.backoffUntil.set(connectionId, this.opts.now() + delay);
        this.opts.log.warn("posting events failed, will retry", {
          connectionId,
          status: result.status,
          attempt,
          delayMs: delay,
        });
        this.schedule(connectionId, delay);
        return;
      }
    }
    const rest = this.queues.get(connectionId);
    if (rest && rest.length > 0) this.schedule(connectionId, 0);
    else this.queues.delete(connectionId);
  }
}
