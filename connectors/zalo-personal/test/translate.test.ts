import { describe, expect, it } from "vitest";
import {
  FriendEventType,
  THREAD_GROUP,
  THREAD_USER,
  translateFriendEvent,
  translateMessage,
  translateUndo,
  type ZcaMessageLike,
} from "../src/translate.ts";

const OWN = "100200300";
const CUSTOMER = "900800700";
const now = () => 42;

function userMsg(over: Partial<ZcaMessageLike["data"]> & { isSelf?: boolean; type?: number } = {}): ZcaMessageLike {
  const { isSelf = false, type = THREAD_USER, ...data } = over;
  return {
    type,
    threadId: CUSTOMER,
    isSelf,
    data: {
      msgId: "7001",
      msgType: "webchat",
      uidFrom: isSelf ? OWN : CUSTOMER,
      idTo: isSelf ? CUSTOMER : OWN,
      dName: "Khách A",
      ts: "1731400000123",
      content: "Cho em hỏi giá",
      ...data,
    },
  };
}

const friends = new Set<string>([CUSTOMER]);
const deps = { isFriend: (uid: string) => friends.has(uid), now };

describe("translateMessage", () => {
  it("maps a text message", () => {
    expect(translateMessage(userMsg(), deps)).toEqual({
      type: "message",
      msgId: "7001",
      threadId: CUSTOMER,
      fromUid: CUSTOMER,
      fromName: "Khách A",
      isSelf: false,
      isFriend: true,
      kind: "text",
      text: "Cho em hỏi giá",
      ts: 1731400000123,
    });
  });

  it("converts numeric msgId to string and flags non-friends", () => {
    const ev = translateMessage(userMsg({ msgId: 123456789 }), { isFriend: () => false, now });
    expect(ev?.msgId).toBe("123456789");
    expect(ev?.isFriend).toBe(false);
  });

  it("maps image with caption, file, sticker and unknown types", () => {
    const img = translateMessage(userMsg({ msgType: "chat.photo", content: { title: "ảnh nè", href: "x" } }), deps);
    expect(img).toMatchObject({ kind: "image", text: "ảnh nè" });
    const imgNoCaption = translateMessage(userMsg({ msgType: "chat.photo", content: { title: "", href: "x" } }), deps);
    expect(imgNoCaption).toMatchObject({ kind: "image", text: null });
    expect(translateMessage(userMsg({ msgType: "share.file", content: { title: "a.pdf" } }), deps)).toMatchObject({
      kind: "file",
      text: null,
    });
    expect(translateMessage(userMsg({ msgType: "chat.sticker", content: { id: 1 } }), deps)).toMatchObject({
      kind: "sticker",
      text: null,
    });
    expect(translateMessage(userMsg({ msgType: "chat.voice", content: { href: "v" } }), deps)).toMatchObject({
      kind: "other",
      text: null,
    });
    expect(
      translateMessage(userMsg({ msgType: "chat.recommended", content: { href: "https://example.com" } }), deps),
    ).toMatchObject({ kind: "other", text: "https://example.com" });
  });

  it("keeps isSelf messages with threadId = customer uid", () => {
    const ev = translateMessage(userMsg({ isSelf: true, dName: "" }), deps);
    expect(ev).toMatchObject({ isSelf: true, threadId: CUSTOMER, fromUid: OWN, fromName: null });
  });

  it("drops group messages", () => {
    expect(translateMessage({ ...userMsg(), type: THREAD_GROUP, threadId: "group-1" }, deps)).toBeNull();
  });

  it("falls back to now() when ts is missing", () => {
    expect(translateMessage(userMsg({ ts: "" }), deps)?.ts).toBe(42);
  });
});

describe("translateUndo", () => {
  it("uses the recalled message id (globalMsgId)", () => {
    const ev = translateUndo(
      {
        threadId: CUSTOMER,
        isSelf: false,
        isGroup: false,
        data: { msgId: "9999", uidFrom: CUSTOMER, ts: "1731400000999", content: { globalMsgId: 7001, deleteMsg: 1 } },
      },
      now,
    );
    expect(ev).toEqual({ type: "undo", msgId: "7001", threadId: CUSTOMER, fromUid: CUSTOMER, ts: 1731400000999 });
  });

  it("drops group undo", () => {
    expect(
      translateUndo({ threadId: "g", isSelf: false, isGroup: true, data: { msgId: "1", uidFrom: "x", ts: "1", content: {} } }),
    ).toBeNull();
  });
});

describe("translateFriendEvent", () => {
  it("maps add/remove/block/unblock with uid from data", () => {
    for (const [type, name] of [
      [FriendEventType.ADD, "add"],
      [FriendEventType.REMOVE, "remove"],
      [FriendEventType.BLOCK, "block"],
      [FriendEventType.UNBLOCK, "unblock"],
    ] as const) {
      expect(translateFriendEvent({ type, threadId: CUSTOMER, isSelf: false, data: CUSTOMER }, OWN, now)).toEqual({
        type: "friend",
        event: name,
        uid: CUSTOMER,
        ts: 42,
      });
    }
  });

  it("maps reject_request to the other party's uid", () => {
    const a = translateFriendEvent(
      { type: FriendEventType.REJECT_REQUEST, threadId: OWN, isSelf: false, data: { fromUid: CUSTOMER, toUid: OWN } },
      OWN,
      now,
    );
    expect(a).toMatchObject({ event: "reject_request", uid: CUSTOMER });
    const b = translateFriendEvent(
      { type: FriendEventType.REJECT_REQUEST, threadId: CUSTOMER, isSelf: true, data: { fromUid: OWN, toUid: CUSTOMER } },
      OWN,
      now,
    );
    expect(b).toMatchObject({ event: "reject_request", uid: CUSTOMER });
  });

  it("drops events we do not forward", () => {
    for (const type of [
      FriendEventType.REQUEST,
      FriendEventType.UNDO_REQUEST,
      FriendEventType.SEEN_FRIEND_REQUEST,
      FriendEventType.PIN_CREATE,
      FriendEventType.UNKNOWN,
    ]) {
      expect(translateFriendEvent({ type, threadId: "x", isSelf: false, data: "x" }, OWN, now)).toBeNull();
    }
  });
});
