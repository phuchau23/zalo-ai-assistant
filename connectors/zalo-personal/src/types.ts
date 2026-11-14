// Shared shapes from CONTRACT.md. Keep in sync with the .NET side.

/** `session` (CONTRACT §2). Never log any part of it. */
export type ZaloSessionData = {
  cookie: unknown;
  imei: string;
  userAgent: string;
};

export type MessageKind = "text" | "image" | "file" | "sticker" | "other";

export type MessageEvent = {
  type: "message";
  msgId: string;
  threadId: string;
  fromUid: string;
  fromName: string | null;
  isSelf: boolean;
  isFriend: boolean;
  kind: MessageKind;
  text: string | null;
  ts: number;
};

export type UndoEvent = {
  type: "undo";
  msgId: string;
  threadId: string;
  fromUid: string;
  ts: number;
};

export type FriendEventName = "add" | "remove" | "reject_request" | "block" | "unblock";

export type FriendEvent = {
  type: "friend";
  event: FriendEventName;
  uid: string;
  ts: number;
};

export type ListenerState =
  | "connected"
  | "reconnecting"
  | "opened_elsewhere"
  | "session_expired"
  | "proxy_failing"
  | "stopped";

export type StateEvent = {
  type: "state";
  state: ListenerState;
  code: number | null;
  detail: string | null;
  ts: number;
};

export type PersonalEvent = MessageEvent | UndoEvent | FriendEvent | StateEvent;

/** States after which the sidecar does not restart the account on its own (CONTRACT §4). */
export const TERMINAL_STATES: ReadonlySet<ListenerState> = new Set<ListenerState>([
  "opened_elsewhere",
  "session_expired",
  "proxy_failing",
]);

export type LoginProgressState = "qr" | "scanned" | "expired" | "declined" | "error";

/** Result of a command sent to a nick (send, find-user...), already mapped to an HTTP answer. */
export type CommandResult = { status: number; body?: unknown };

/**
 * `replyTo` = msgId of the customer message this text answers. If a newer customer message arrived in that thread (before or during
 * the typing delay), the worker does not send and answers 409 `superseded` (CONTRACT §5).
 */
export type SendArgs = { threadId: string; text: string; typingMs: number; replyTo?: string | null };

export type FakeSimulation = {
  action: string;
  loginId?: string;
  connectionId?: string;
  uid?: string;
  displayName?: string;
  fromUid?: string;
  fromName?: string;
  toUid?: string;
  text?: string;
  kind?: string;
  isFriend?: boolean;
  group?: boolean;
  event?: string;
  code?: number;
};

// ---------------------------------------------------------------------------
// IPC between supervisor (parent) and worker (child process)
// ---------------------------------------------------------------------------

export type WorkerInit =
  | {
      role: "login";
      loginId: string;
      userAgent: string;
      proxyUrl: string | null;
      mode: "real" | "fake";
    }
  | {
      role: "account";
      connectionId: string;
      session: ZaloSessionData;
      proxyUrl: string | null;
      mode: "real" | "fake";
    };

export type WorkerCommand =
  | { cmd: "send"; args: SendArgs }
  | { cmd: "findUser"; args: { phone: string } }
  | { cmd: "friendRequest"; args: { uid: string; message: string } }
  | { cmd: "undoFriendRequest"; args: { uid: string } }
  | { cmd: "simulate"; args: FakeSimulation };

export type ParentToWorker =
  | { type: "init"; init: WorkerInit }
  | { type: "cmd"; id: number; command: WorkerCommand }
  | { type: "loginAccepted"; connectionId: string }
  | { type: "loginRejected" }
  | { type: "stop" };

export type WorkerToParent =
  | { type: "ready" }
  | { type: "event"; event: PersonalEvent }
  | { type: "loginProgress"; state: LoginProgressState; qrPng?: string; error?: string }
  | {
      type: "loginResult";
      uid: string;
      displayName: string;
      avatarUrl: string | null;
      session: ZaloSessionData;
    }
  | { type: "session"; session: ZaloSessionData }
  | { type: "reply"; id: number; result: CommandResult };

/** Exit codes used by the worker so the supervisor can tell "done" from "crashed". */
export const EXIT_OK = 0;
export const EXIT_CRASH = 2;
