// FakeZaloSession (REMI_MODE=fake, dev only, CONTRACT §7). Emits zca-js-shaped objects so the same
// translate.ts path (group drop, kinds, isSelf) runs as with the real library. Deterministic and fast.
import { createHash } from "node:crypto";
import { FriendEventType, THREAD_GROUP, THREAD_USER, type ZcaMessageLike } from "../translate.ts";
import type { FakeSimulation, ZaloSessionData } from "../types.ts";
import {
  QrDeclinedError,
  QrExpiredError,
  type FakeControls,
  type FoundUser,
  type ListenerHandlers,
  type LoginOutcome,
  type QrHandlers,
  type ZaloSession,
} from "./zalo-session.ts";

/** Fixed QR image (116×116 PNG, QR-looking pattern), base64 without data: prefix. */
export const FAKE_QR_PNG =
  "iVBORw0KGgoAAAANSUhEUgAAAHQAAAB0AQAAAAB84SuKAAAAjUlEQVR42u3VsQ2AMAxEUW9w+2/pDQ45NomhO7eQItJrovgjBTMzAO4ErJZkkCknyIENoNERB2LoXGOv42xo8u6x51McCavH7qt4TZI9SPrMew3s9Tlyk13D1Ka7C3DZzwvp/vt9u9+6Reuhej2hvY/oeE96D9nnPcseAz/6DNx7yI553j0E5y/s9BB9AXwCApGLbAsYAAAAAElFTkSuQmCC";

/** `fake-<6 digits>` derived from the loginId (CONTRACT §7 default uid). */
export function defaultFakeUid(seed: string): string {
  const h = createHash("sha256").update(seed).digest();
  return `fake-${String(h.readUInt32BE(0) % 1_000_000).padStart(6, "0")}`;
}

/** Error shape the classifier maps to 422 rejected. */
export class FakeRejectedError extends Error {
  readonly code: number;
  constructor(code: number, reason: string) {
    super(reason);
    this.name = "FakeRejected";
    this.code = code;
  }
}

/** Error shape the classifier maps to 503 unavailable. */
export class FakeNetworkError extends Error {
  readonly code = "ECONNRESET";
  constructor() {
    super("fake network down");
    this.name = "FetchError";
  }
}

const FRIEND_NAME_TO_TYPE: Record<string, number> = {
  add: FriendEventType.ADD,
  remove: FriendEventType.REMOVE,
  reject_request: FriendEventType.REJECT_REQUEST,
  block: FriendEventType.BLOCK,
  unblock: FriendEventType.UNBLOCK,
};

export type FakeTimings = {
  qrDelayMs: number;
  scanToLoginMs: number;
  connectDelayMs: number;
  echoDelayMs: number;
  rejectFriendMs: number;
  acceptFriendMs: number;
  reconnectMs: number;
};

export const DEFAULT_FAKE_TIMINGS: FakeTimings = {
  qrDelayMs: 10,
  scanToLoginMs: 50,
  connectDelayMs: 10,
  echoDelayMs: 50,
  rejectFriendMs: 1000,
  acceptFriendMs: 2000,
  reconnectMs: 1000,
};

export class FakeZaloSession implements ZaloSession, FakeControls {
  private session: ZaloSessionData | null = null;
  private uid: string | null = null;
  private handlers: ListenerHandlers | null = null;
  private readonly timers = new Set<ReturnType<typeof setTimeout>>();
  private readonly friends = new Set<string>();
  private msgCounter = 0;
  private readonly msgBase: number;
  private pendingQr: {
    userAgent: string;
    handlers: QrHandlers;
    resolve: (o: LoginOutcome) => void;
    reject: (e: Error) => void;
  } | null = null;
  private readonly t: FakeTimings;

  constructor(timings: Partial<FakeTimings> = {}) {
    this.t = { ...DEFAULT_FAKE_TIMINGS, ...timings };
    this.msgBase = Date.now() * 10;
  }

  private later(ms: number, fn: () => void): void {
    const timer = setTimeout(() => {
      this.timers.delete(timer);
      fn();
    }, ms);
    this.timers.add(timer);
  }

  private nextMsgId(): string {
    this.msgCounter += 1;
    return String(this.msgBase + this.msgCounter);
  }

  // ---- login -------------------------------------------------------------

  loginQR(userAgent: string, handlers: QrHandlers): Promise<LoginOutcome> {
    return new Promise<LoginOutcome>((resolve, reject) => {
      this.pendingQr = { userAgent, handlers, resolve, reject };
      this.later(this.t.qrDelayMs, () => handlers.onQr(FAKE_QR_PNG));
    });
  }

  simulateQr(action: "scan" | "decline" | "expire", uid?: string, displayName?: string): boolean {
    const p = this.pendingQr;
    if (!p) return false;
    this.pendingQr = null;
    if (action === "decline") {
      p.reject(new QrDeclinedError());
      return true;
    }
    if (action === "expire") {
      p.reject(new QrExpiredError());
      return true;
    }
    p.handlers.onScanned();
    const finalUid = uid && uid.length > 0 ? uid : defaultFakeUid(p.userAgent + Date.now());
    const name = displayName && displayName.length > 0 ? displayName : `Fake ${finalUid.slice(-4)}`;
    const session: ZaloSessionData = {
      cookie: [
        { key: "zpw_sek", value: `fake-sek-${finalUid}`, domain: "chat.zalo.me", path: "/" },
        { key: "fake_uid", value: finalUid, domain: "chat.zalo.me", path: "/" },
      ],
      imei: `fake-imei-${finalUid}`,
      userAgent: p.userAgent,
    };
    this.later(this.t.scanToLoginMs, () => {
      this.session = session;
      this.uid = finalUid;
      p.resolve({ uid: finalUid, displayName: name, avatarUrl: null, session });
    });
    return true;
  }

  async loginWithSession(session: ZaloSessionData): Promise<void> {
    this.session = session;
    let uid: string | null = null;
    if (Array.isArray(session.cookie)) {
      for (const c of session.cookie) {
        if (c && typeof c === "object" && (c as { key?: unknown }).key === "fake_uid") {
          const v = (c as { value?: unknown }).value;
          if (typeof v === "string") uid = v;
        }
      }
    }
    this.uid = uid ?? "fake-self";
  }

  ownUid(): string | null {
    return this.uid;
  }

  // ---- listener ----------------------------------------------------------

  startListener(handlers: ListenerHandlers): void {
    this.handlers = handlers;
    this.later(this.t.connectDelayMs, () => this.handlers?.onConnected());
  }

  async listFriendIds(): Promise<string[]> {
    return [...this.friends];
  }

  async sendTyping(_threadId: string): Promise<void> {}

  async sendText(threadId: string, text: string): Promise<string> {
    if (text.includes("[[down]]")) throw new FakeNetworkError();
    if (text.includes("[[blocked]]")) {
      throw new FakeRejectedError(114, "fake: recipient does not accept messages from strangers");
    }
    const msgId = this.nextMsgId();
    // Real zca-js (selfListen: true) echoes our own send back as an isSelf message.
    this.later(this.t.echoDelayMs, () => {
      this.handlers?.onMessage(this.buildUserMessage({ msgId, threadId, isSelf: true, text, kind: "text" }));
    });
    return msgId;
  }

  async findUser(phone: string): Promise<FoundUser | null> {
    if (phone.endsWith("000")) return null;
    return { uid: `fake-${phone}`, displayName: `Fake ${phone.slice(-4)}`, avatarUrl: null };
  }

  async sendFriendRequest(uid: string, _message: string): Promise<void> {
    if (uid.includes("block")) {
      this.later(this.t.rejectFriendMs, () =>
        this.handlers?.onFriendEvent({
          type: FriendEventType.REJECT_REQUEST,
          threadId: uid,
          isSelf: false,
          data: { fromUid: uid, toUid: this.uid ?? "fake-self" },
        }),
      );
    } else {
      this.later(this.t.acceptFriendMs, () => {
        this.friends.add(uid);
        this.handlers?.onFriendEvent({ type: FriendEventType.ADD, threadId: uid, isSelf: false, data: uid });
      });
    }
  }

  async undoFriendRequest(_uid: string): Promise<void> {}

  currentSession(): ZaloSessionData | null {
    return this.session;
  }

  stop(): void {
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
    this.handlers = null;
    if (this.pendingQr) {
      const p = this.pendingQr;
      this.pendingQr = null;
      p.reject(new QrExpiredError());
    }
  }

  // ---- simulation --------------------------------------------------------

  private buildUserMessage(o: {
    msgId: string;
    threadId: string;
    isSelf: boolean;
    text: string | null;
    kind: string;
    fromName?: string | null;
    group?: boolean;
    fromUid?: string;
  }): ZcaMessageLike {
    let msgType = "webchat";
    let content: unknown = o.text ?? "";
    switch (o.kind) {
      case "image":
        msgType = "chat.photo";
        content = { title: o.text ?? "", href: "https://example.invalid/fake.jpg", thumb: "" };
        break;
      case "file":
        msgType = "share.file";
        content = { title: "fake.pdf", href: "https://example.invalid/fake.pdf" };
        break;
      case "sticker":
        msgType = "chat.sticker";
        content = { id: 1, catId: 1, type: 7 };
        break;
      case "other":
        msgType = "chat.location.new";
        content = {};
        break;
    }
    const own = this.uid ?? "fake-self";
    return {
      type: o.group ? THREAD_GROUP : THREAD_USER,
      threadId: o.group ? "fake-group-1" : o.threadId,
      isSelf: o.isSelf,
      data: {
        msgId: o.msgId,
        msgType,
        uidFrom: o.isSelf ? own : (o.fromUid ?? o.threadId),
        idTo: o.isSelf ? o.threadId : own,
        dName: o.fromName ?? "",
        ts: String(Date.now()),
        content,
      },
    };
  }

  simulate(sim: FakeSimulation): void {
    const h = this.handlers;
    if (!h) return;
    switch (sim.action) {
      case "incoming": {
        const fromUid = sim.fromUid ?? "fake-customer";
        if (sim.isFriend === true) this.friends.add(fromUid);
        if (sim.isFriend === false) this.friends.delete(fromUid);
        h.onMessage(
          this.buildUserMessage({
            msgId: this.nextMsgId(),
            threadId: fromUid,
            fromUid,
            isSelf: false,
            text: sim.text ?? null,
            kind: sim.kind ?? "text",
            fromName: sim.fromName ?? null,
            group: sim.group === true,
          }),
        );
        break;
      }
      case "self_message":
        h.onMessage(
          this.buildUserMessage({
            msgId: this.nextMsgId(),
            threadId: sim.toUid ?? "fake-customer",
            isSelf: true,
            text: sim.text ?? "",
            kind: "text",
          }),
        );
        break;
      case "friend": {
        const uid = sim.uid ?? "fake-customer";
        const type = FRIEND_NAME_TO_TYPE[sim.event ?? ""];
        if (type === undefined) return;
        if (type === FriendEventType.ADD) this.friends.add(uid);
        if (type === FriendEventType.REMOVE) this.friends.delete(uid);
        h.onFriendEvent({
          type,
          threadId: uid,
          isSelf: false,
          data: type === FriendEventType.REJECT_REQUEST ? { fromUid: uid, toUid: this.uid ?? "fake-self" } : uid,
        });
        break;
      }
      case "close": {
        const code = sim.code ?? 1006;
        h.onDisconnected(code, "fake close");
        if (code === 1006) {
          // zca-js retryOnClose: reconnects by itself.
          this.later(this.t.reconnectMs, () => this.handlers?.onConnected());
        } else {
          // 3000/3003 (and any other code): no automatic retry.
          h.onClosed(code, "fake close");
        }
        break;
      }
    }
  }
}
