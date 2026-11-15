// ZaloSession backed by zca-js 2.2.0 (unofficial library — see README risk warning).
import { Zalo, ThreadType, DestType, LoginQRCallbackEventType, type API } from "zca-js";
import { zcaNetworkOptions } from "../proxy.ts";
import { isNetworkError, isZcaApiError } from "../errors.ts";
import type { ZcaFriendEventLike, ZcaMessageLike, ZcaUndoLike } from "../translate.ts";
import type { ZaloSessionData } from "../types.ts";
import {
  QrDeclinedError,
  QrExpiredError,
  SessionExpiredError,
  type FoundUser,
  type ListenerHandlers,
  type LoginOutcome,
  type QrHandlers,
  type ZaloSession,
} from "./zalo-session.ts";

/**
 * Errors from `zalo.login(credentials)` that mean "Zalo refused this session" (→ session_expired)
 * versus transport problems (→ retry / proxy check). zca-js throws:
 * - "Đăng nhập thất bại" / "Failed to fetch server info: <zalo msg>" / "Missing required params" → refused
 * - "Failed to fetch login info: <statusText>" (HTTP not OK) and fetch/socket errors → network
 */
export function isSessionRefused(err: unknown): boolean {
  if (isNetworkError(err)) return false;
  if (!isZcaApiError(err)) return false;
  const m = (err as { message?: unknown }).message;
  if (typeof m === "string" && m.startsWith("Failed to fetch login info")) return false;
  return true;
}

export class RealZaloSession implements ZaloSession {
  private readonly zalo: Zalo;
  private api: API | null = null;
  private loginCredentials: ZaloSessionData | null = null;

  constructor(proxyUrl: string | null) {
    this.zalo = new Zalo({
      selfListen: true,
      checkUpdate: false,
      logging: false,
      ...zcaNetworkOptions(proxyUrl),
    });
  }

  async loginQR(userAgent: string, handlers: QrHandlers): Promise<LoginOutcome> {
    let gotInfo: { cookie: unknown; imei: string; userAgent: string } | null = null;
    let scannedName: string | null = null;
    let scannedAvatar: string | null = null;
    let declined = false;
    let expired = false;

    let api: API;
    try {
      api = await this.zalo.loginQR({ userAgent, language: "vi" }, (event) => {
        switch (event.type) {
          case LoginQRCallbackEventType.QRCodeGenerated:
            // zca-js already strips the "data:image/png;base64," prefix.
            handlers.onQr(event.data.image.replace(/^data:image\/png;base64,/, ""));
            break;
          case LoginQRCallbackEventType.QRCodeScanned:
            scannedName = event.data?.display_name ?? null;
            scannedAvatar = event.data?.avatar ?? null;
            handlers.onScanned();
            break;
          case LoginQRCallbackEventType.QRCodeExpired:
            // With a callback zca-js waits for us to call retry() or abort().
            if (handlers.onExpired()) {
              event.actions.retry();
            } else {
              expired = true;
              event.actions.abort();
            }
            break;
          case LoginQRCallbackEventType.QRCodeDeclined:
            // zca-js leaves the promise pending after a decline unless we abort.
            declined = true;
            event.actions.abort();
            break;
          case LoginQRCallbackEventType.GotLoginInfo:
            gotInfo = { cookie: event.data.cookie, imei: event.data.imei, userAgent: event.data.userAgent };
            break;
        }
      });
    } catch (err) {
      if (declined) throw new QrDeclinedError();
      if (expired) throw new QrExpiredError();
      throw err;
    }
    this.api = api;
    const info = gotInfo as { cookie: unknown; imei: string; userAgent: string } | null;
    if (!info) throw new Error("login info missing");
    this.loginCredentials = { cookie: info.cookie, imei: info.imei, userAgent: info.userAgent };

    const uid = String(api.getOwnId());
    let displayName = scannedName ?? "";
    let avatarUrl: string | null = scannedAvatar;
    try {
      const me = await api.fetchAccountInfo();
      if (me?.profile?.displayName) displayName = me.profile.displayName;
      if (me?.profile?.avatar) avatarUrl = me.profile.avatar;
    } catch {
      // keep what the scan event gave us
    }
    return { uid, displayName, avatarUrl, session: this.loginCredentials };
  }

  async loginWithSession(session: ZaloSessionData): Promise<void> {
    try {
      this.api = await this.zalo.login({
        cookie: session.cookie as never,
        imei: session.imei,
        userAgent: session.userAgent,
        language: "vi",
      });
      this.loginCredentials = session;
    } catch (err) {
      if (isSessionRefused(err)) throw new SessionExpiredError();
      throw err;
    }
  }

  ownUid(): string | null {
    if (!this.api) return null;
    const id = this.api.getOwnId();
    return id ? String(id) : null;
  }

  private requireApi(): API {
    if (!this.api) throw new Error("not logged in");
    return this.api;
  }

  startListener(h: ListenerHandlers): void {
    const listener = this.requireApi().listener;
    listener.on("connected", () => h.onConnected());
    listener.on("disconnected", (code, reason) => h.onDisconnected(Number(code), String(reason ?? "")));
    listener.on("closed", (code, reason) => h.onClosed(Number(code), String(reason ?? "")));
    listener.on("error", (err) => h.onError(err));
    listener.on("message", (msg) => h.onMessage(msg as unknown as ZcaMessageLike));
    listener.on("undo", (undo) => h.onUndo(undo as unknown as ZcaUndoLike));
    listener.on("friend_event", (ev) => h.onFriendEvent(ev as unknown as ZcaFriendEventLike));
    listener.start({ retryOnClose: true });
  }

  async listFriendIds(): Promise<string[]> {
    const friends = await this.requireApi().getAllFriends();
    return Array.isArray(friends) ? friends.map((f) => String(f.userId)).filter((s) => s.length > 0) : [];
  }

  async sendTyping(threadId: string): Promise<void> {
    await this.requireApi().sendTypingEvent(threadId, ThreadType.User, DestType.User);
  }

  async sendText(threadId: string, text: string): Promise<string> {
    const res = await this.requireApi().sendMessage(text, threadId, ThreadType.User);
    const msgId = res?.message?.msgId;
    if (msgId === undefined || msgId === null) throw new Error("no msgId in send response");
    return String(msgId);
  }

  async findUser(phone: string): Promise<FoundUser | null> {
    const u = await this.requireApi().findUser(phone);
    if (!u || !u.uid) return null;
    return {
      uid: String(u.uid),
      displayName: u.display_name || u.zalo_name || "",
      avatarUrl: u.avatar || null,
    };
  }

  async sendFriendRequest(uid: string, message: string): Promise<void> {
    await this.requireApi().sendFriendRequest(message, uid);
  }

  async undoFriendRequest(uid: string): Promise<void> {
    await this.requireApi().undoFriendRequest(uid);
  }

  /** Read the live cookie jar back from zca-js (it is updated from Set-Cookie on every request). */
  currentSession(): ZaloSessionData | null {
    if (!this.api) return this.loginCredentials;
    const ctx = this.api.getContext();
    const jar = ctx.cookie?.toJSON();
    if (!jar || !Array.isArray(jar.cookies)) return this.loginCredentials;
    return { cookie: jar.cookies, imei: ctx.imei, userAgent: ctx.userAgent };
  }

  stop(): void {
    try {
      this.api?.listener.stop();
    } catch {
      // already stopped
    }
  }
}
