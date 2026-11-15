// Idempotency key store + "just sent" set (CONTRACT §5, §4 isSelf rule).
// - messageId (uuid from the API) → msgId (Zalo) for 1 hour: a retried send returns the old msgId.
// - Concurrent duplicates while the first send is still in flight share the same promise.
// - Recently sent msgIds (per connection) so the echo that zca-js delivers as isSelf=true is dropped.

export const IDEMPOTENCY_TTL_MS = 60 * 60 * 1000;
export const SENT_TTL_MS = 10 * 60 * 1000;

type Entry = { msgId: string; expiresAt: number };

export class SentCache {
  private readonly byMessageId = new Map<string, Entry>();
  private readonly inflight = new Map<string, Promise<unknown>>();
  private readonly recentSent = new Map<string, number>(); // `${connectionId}:${msgId}` → expiresAt
  private readonly now: () => number;

  constructor(now: () => number = Date.now) {
    this.now = now;
  }

  private key(connectionId: string, messageId: string): string {
    return `${connectionId}:${messageId}`;
  }

  /** msgId previously returned for this messageId within the TTL. */
  get(connectionId: string, messageId: string): string | undefined {
    const k = this.key(connectionId, messageId);
    const e = this.byMessageId.get(k);
    if (!e) return undefined;
    if (e.expiresAt <= this.now()) {
      this.byMessageId.delete(k);
      return undefined;
    }
    return e.msgId;
  }

  /**
   * Run `work` at most once per (connectionId, messageId) at a time; callers arriving while it runs
   * share the result. The caller decides what to remember on success.
   */
  async once<T>(connectionId: string, messageId: string, work: () => Promise<T>): Promise<T> {
    const k = this.key(connectionId, messageId);
    const existing = this.inflight.get(k);
    if (existing) return existing as Promise<T>;
    const p = (async () => {
      try {
        return await work();
      } finally {
        this.inflight.delete(k);
      }
    })();
    this.inflight.set(k, p);
    return p;
  }

  /** Record a successful send: idempotency entry + echo-suppression entry. */
  remember(connectionId: string, messageId: string, msgId: string): void {
    const now = this.now();
    this.byMessageId.set(this.key(connectionId, messageId), { msgId, expiresAt: now + IDEMPOTENCY_TTL_MS });
    this.markSent(connectionId, msgId);
    this.prune();
  }

  markSent(connectionId: string, msgId: string): void {
    this.recentSent.set(`${connectionId}:${msgId}`, this.now() + SENT_TTL_MS);
  }

  wasSentByUs(connectionId: string, msgId: string): boolean {
    const k = `${connectionId}:${msgId}`;
    const exp = this.recentSent.get(k);
    if (exp === undefined) return false;
    if (exp <= this.now()) {
      this.recentSent.delete(k);
      return false;
    }
    return true;
  }

  prune(): void {
    const now = this.now();
    for (const [k, e] of this.byMessageId) if (e.expiresAt <= now) this.byMessageId.delete(k);
    for (const [k, exp] of this.recentSent) if (exp <= now) this.recentSent.delete(k);
  }

  size(): { idempotency: number; sent: number } {
    return { idempotency: this.byMessageId.size, sent: this.recentSent.size };
  }
}
