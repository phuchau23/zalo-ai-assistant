// Anti-corruption layer: zca-js objects → PersonalEvent (CONTRACT §4).
// Only this file knows zca-js message/undo/friend-event shapes. The types below are the
// structural subset we read (checked against zca-js 2.2.0 dist/models), so tests and the fake
// session can build them without importing zca-js.
import type { FriendEvent, FriendEventName, MessageEvent, MessageKind, UndoEvent } from "./types.ts";

/** zca-js ThreadType: User = 0, Group = 1. */
export const THREAD_USER = 0;
export const THREAD_GROUP = 1;

/** Subset of zca-js UserMessage | GroupMessage. */
export type ZcaMessageLike = {
  type: number; // ThreadType
  threadId: string;
  isSelf: boolean;
  data: {
    msgId: string | number;
    msgType?: string;
    uidFrom: string;
    idTo?: string;
    dName?: string;
    ts: string | number;
    content: unknown;
  };
};

/** Subset of zca-js Undo. */
export type ZcaUndoLike = {
  threadId: string;
  isSelf: boolean;
  isGroup: boolean;
  data: {
    msgId: string | number;
    uidFrom: string;
    ts: string | number;
    content: { globalMsgId?: number | string; cliMsgId?: number | string } & Record<string, unknown>;
  };
};

/** zca-js FriendEventType values (dist/models/FriendEvent.js). */
export const FriendEventType = {
  ADD: 0,
  REMOVE: 1,
  REQUEST: 2,
  UNDO_REQUEST: 3,
  REJECT_REQUEST: 4,
  SEEN_FRIEND_REQUEST: 5,
  BLOCK: 6,
  UNBLOCK: 7,
  BLOCK_CALL: 8,
  UNBLOCK_CALL: 9,
  PIN_UNPIN: 10,
  PIN_CREATE: 11,
  UNKNOWN: 12,
} as const;

/** Subset of zca-js FriendEvent. */
export type ZcaFriendEventLike = {
  type: number;
  threadId: string;
  isSelf: boolean;
  data: unknown;
};

function toTs(value: string | number | undefined, now: () => number): number {
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) && n > 0 ? n : now();
}

function str(value: unknown): string | null {
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "bigint") return String(value);
  return null;
}

/** Map zca-js msgType/content to our kind + text. */
export function classifyContent(msgType: string | undefined, content: unknown): { kind: MessageKind; text: string | null } {
  const type = msgType ?? "";
  if (typeof content === "string") {
    // "webchat" is plain text; any other msgType carrying a plain string is treated as text too.
    return { kind: "text", text: content };
  }
  const obj = content && typeof content === "object" ? (content as Record<string, unknown>) : {};
  switch (type) {
    case "chat.photo": {
      // Caption, if any, is in `title`.
      const caption = typeof obj.title === "string" && obj.title.trim().length > 0 ? obj.title : null;
      return { kind: "image", text: caption };
    }
    case "share.file":
      return { kind: "file", text: null };
    case "chat.sticker":
      return { kind: "sticker", text: null };
    case "chat.recommended":
    case "chat.link": {
      // Link share: keep the URL so staff can see what was shared.
      const href = typeof obj.href === "string" && obj.href.length > 0 ? obj.href : null;
      return { kind: "other", text: href };
    }
    default:
      return { kind: "other", text: null };
  }
}

export type TranslateDeps = {
  isFriend: (uid: string) => boolean;
  now?: () => number;
};

/** Returns null for group messages (CONTRACT: never sent upstream). */
export function translateMessage(msg: ZcaMessageLike, deps: TranslateDeps): MessageEvent | null {
  if (msg.type !== THREAD_USER) return null;
  const now = deps.now ?? Date.now;
  const msgId = str(msg.data.msgId);
  const threadId = str(msg.threadId);
  if (!msgId || !threadId) return null;
  const { kind, text } = classifyContent(msg.data.msgType, msg.data.content);
  const fromName = typeof msg.data.dName === "string" && msg.data.dName.length > 0 ? msg.data.dName : null;
  return {
    type: "message",
    msgId,
    threadId,
    fromUid: str(msg.data.uidFrom) ?? threadId,
    fromName,
    isSelf: msg.isSelf === true,
    isFriend: deps.isFriend(threadId),
    kind,
    text,
    ts: toTs(msg.data.ts, now),
  };
}

/**
 * Undo (recall). `msgId` is the id of the message being recalled (content.globalMsgId), so the API
 * can match it with the stored message; zca-js `data.msgId` is the id of the recall action itself.
 */
export function translateUndo(undo: ZcaUndoLike, now: () => number = Date.now): UndoEvent | null {
  if (undo.isGroup) return null;
  const recalled = str(undo.data.content?.globalMsgId) ?? str(undo.data.msgId);
  const threadId = str(undo.threadId);
  if (!recalled || !threadId) return null;
  return {
    type: "undo",
    msgId: recalled,
    threadId,
    fromUid: str(undo.data.uidFrom) ?? threadId,
    ts: toTs(undo.data.ts, now),
  };
}

const FRIEND_MAP: ReadonlyMap<number, FriendEventName> = new Map<number, FriendEventName>([
  [FriendEventType.ADD, "add"],
  [FriendEventType.REMOVE, "remove"],
  [FriendEventType.REJECT_REQUEST, "reject_request"],
  [FriendEventType.BLOCK, "block"],
  [FriendEventType.UNBLOCK, "unblock"],
]);

/**
 * Friend events we forward: add/remove/reject_request/block/unblock. Others (incoming requests,
 * seen, pins, call blocks, unknown) are dropped. For reject events the "other" uid is whichever of
 * fromUid/toUid is not our own uid.
 */
export function translateFriendEvent(
  ev: ZcaFriendEventLike,
  ownUid: string | null,
  now: () => number = Date.now,
): FriendEvent | null {
  const name = FRIEND_MAP.get(ev.type);
  if (!name) return null;
  let uid: string | null = null;
  if (ev.type === FriendEventType.REJECT_REQUEST) {
    const d = (ev.data ?? {}) as { fromUid?: unknown; toUid?: unknown };
    const from = str(d.fromUid);
    const to = str(d.toUid);
    uid = ownUid && from === ownUid ? to : from && from !== ownUid ? from : to;
  } else {
    uid = str(ev.data) ?? str(ev.threadId);
  }
  if (!uid) return null;
  return { type: "friend", event: name, uid, ts: now() };
}
