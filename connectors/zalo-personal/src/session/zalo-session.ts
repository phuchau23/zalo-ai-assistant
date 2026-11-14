// Strategy interface: RealZaloSession (zca-js) and FakeZaloSession (REMI_MODE=fake) implement it.
// The worker only talks to this interface; zca-js object shapes stop at translate.ts.
import { createHash } from "node:crypto";
import type { ZcaFriendEventLike, ZcaMessageLike, ZcaUndoLike } from "../translate.ts";
import type { FakeSimulation, ZaloSessionData } from "../types.ts";

export type ListenerHandlers = {
  onConnected(): void;
  onDisconnected(code: number, reason: string): void;
  /** Listener gave up (no more automatic retries). */
  onClosed(code: number, reason: string): void;
  onError(err: unknown): void;
  onMessage(msg: ZcaMessageLike): void;
  onUndo(undo: ZcaUndoLike): void;
  onFriendEvent(ev: ZcaFriendEventLike): void;
};

export type QrHandlers = {
  /** PNG, base64 without the data: prefix. */
  onQr(pngBase64: string): void;
  onScanned(): void;
  /** QR timed out: return true to generate a new QR, false to give up. */
  onExpired(): boolean;
};

export type LoginOutcome = {
  uid: string;
  displayName: string;
  avatarUrl: string | null;
  session: ZaloSessionData;
};

export type FoundUser = { uid: string; displayName: string; avatarUrl: string | null };

/** QR login ended by the user declining on the phone. */
export class QrDeclinedError extends Error {
  constructor() {
    super("qr declined");
    this.name = "QrDeclinedError";
  }
}

/** QR login ended because it expired (or was aborted after expiring). */
export class QrExpiredError extends Error {
  constructor() {
    super("qr expired");
    this.name = "QrExpiredError";
  }
}

/** Login with a stored session failed for a reason that is not the network: the owner must scan again. */
export class SessionExpiredError extends Error {
  constructor() {
    super("session expired");
    this.name = "SessionExpiredError";
  }
}

export interface ZaloSession {
  loginQR(userAgent: string, handlers: QrHandlers): Promise<LoginOutcome>;
  /** Throws SessionExpiredError when Zalo refuses the session; other errors are network/temporary. */
  loginWithSession(session: ZaloSessionData): Promise<void>;
  ownUid(): string | null;
  startListener(handlers: ListenerHandlers): void;
  listFriendIds(): Promise<string[]>;
  sendTyping(threadId: string): Promise<void>;
  /** Returns the Zalo msgId (as string). */
  sendText(threadId: string, text: string): Promise<string>;
  /** null when no Zalo user has that phone. */
  findUser(phone: string): Promise<FoundUser | null>;
  sendFriendRequest(uid: string, message: string): Promise<void>;
  undoFriendRequest(uid: string): Promise<void>;
  /** The live session (cookie may have been refreshed by Zalo). */
  currentSession(): ZaloSessionData | null;
  stop(): void;
}

/** Extra hooks only the fake session has (POST /dev/simulate). */
export interface FakeControls {
  simulateQr(action: "scan" | "decline" | "expire", uid?: string, displayName?: string): boolean;
  simulate(sim: FakeSimulation): void;
}

export function isFakeControls(s: unknown): s is FakeControls {
  return (
    !!s &&
    typeof (s as FakeControls).simulateQr === "function" &&
    typeof (s as FakeControls).simulate === "function"
  );
}

/**
 * Stable fingerprint of a session for "did the cookie change?" checks. Only cookie name/value/
 * domain/path/expiry count: tough-cookie also serialises `creation`/`lastAccessed`, which change on
 * every request and would make every check look like a change.
 */
export function sessionFingerprint(session: ZaloSessionData | null): string {
  if (!session) return "";
  const raw = session.cookie as unknown;
  const list: unknown[] = Array.isArray(raw)
    ? raw
    : raw && typeof raw === "object" && Array.isArray((raw as { cookies?: unknown }).cookies)
      ? ((raw as { cookies: unknown[] }).cookies)
      : [];
  const parts: string[] = [];
  for (const c of list) {
    if (!c || typeof c !== "object") continue;
    const o = c as Record<string, unknown>;
    const name = String(o.key ?? o.name ?? "");
    let domain = String(o.domain ?? "");
    if (domain.startsWith(".")) domain = domain.slice(1);
    const expires = o.expires ?? o.expirationDate ?? "";
    parts.push([name, String(o.value ?? ""), domain, String(o.path ?? ""), String(expires)].join("\u0001"));
  }
  parts.sort();
  return createHash("sha256")
    .update(parts.join("\u0002"))
    .update("\u0003" + session.imei + "\u0003" + session.userAgent)
    .digest("hex");
}
