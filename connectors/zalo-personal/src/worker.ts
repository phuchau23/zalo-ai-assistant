// Worker = one child process per nick (or per pending QR login). Holds the ZaloSession (zca-js objects,
// cookie) in its own memory only; talks to the supervisor over IPC. A crash here cannot take down
// other nicks. Exit code EXIT_OK = finished on purpose (stop, terminal state, login ended);
// EXIT_CRASH = abnormal, the supervisor restarts it with backoff if the nick is still wanted.
import { createLogger, errFields, registerSecret, type Logger, type LogLevel } from "./log.ts";
import { errorToResult, isNetworkError, zaloErrorCode } from "./errors.ts";
import { translateFriendEvent, translateMessage, translateUndo } from "./translate.ts";
import { testProxy, fakeTestProxy, type ProxyTestResult } from "./proxy.ts";
import {
  QrDeclinedError,
  QrExpiredError,
  SessionExpiredError,
  isFakeControls,
  sessionFingerprint,
  type ZaloSession,
} from "./session/zalo-session.ts";
import { defaultFakeUid } from "./session/fake-session.ts";
import {
  EXIT_CRASH,
  EXIT_OK,
  type CommandResult,
  type FakeSimulation,
  type ListenerState,
  type ParentToWorker,
  type WorkerCommand,
  type WorkerInit,
  type WorkerToParent,
  type ZaloSessionData,
} from "./types.ts";

export const MAX_TYPING_MS = 15_000;
export const LOGIN_TIMEOUT_MS = 5 * 60_000;
/** Generate a new QR on expiry only if at least this much of the login window is left. */
export const QR_RETRY_MIN_REMAINING_MS = 30_000;
export const SESSION_CHECK_INTERVAL_MS = 6 * 60 * 60_000;
/**
 * How often the live cookie jar is compared with the copy the API holds. Cheap (no network). The QR login hands us the cookies from
 * BEFORE zca-js finished logging in; Zalo rotates them during login, and the pre-login copy is refused on the next start — found in
 * the first real-nick test (2026-11-13: restart → session_expired). So we also push right after going live and on every reconnect.
 */
export const SESSION_PUSH_INTERVAL_MS = 10 * 60_000;
export const FRIENDS_RETRY_MS = 5 * 60_000;
/** Threads remembered for "latest customer message" (oldest forgotten first). Only used to drop superseded bot replies. */
export const LATEST_INCOMING_MAX = 5_000;

export type WorkerIO = {
  send(msg: WorkerToParent): void;
  exit(code: number): void;
};

export type WorkerDeps = {
  io: WorkerIO;
  log: Logger;
  createSession: (init: WorkerInit) => Promise<ZaloSession>;
  proxyTest?: (proxyUrl: string) => Promise<ProxyTestResult>;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
  sessionCheckIntervalMs?: number;
  exitDelayMs?: number;
};

const defaultSleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** CONTRACT §5: the bot reply is stale (customer wrote again) → not sent, the API marks it skipped. Never cached as sent. */
const SUPERSEDED: CommandResult = { status: 409, body: { error: "superseded" } };

export class WorkerCore {
  private readonly io: WorkerIO;
  private log: Logger;
  private readonly deps: WorkerDeps;
  private readonly now: () => number;
  private readonly sleep: (ms: number) => Promise<void>;
  private init: WorkerInit | null = null;
  private session: ZaloSession | null = null;
  private connectionId: string | null = null;
  private proxyUrl: string | null = null;
  private ready = false;
  private exiting = false;
  private readonly friends = new Set<string>();
  /** threadId → msgId of the newest message the customer sent there (not our own). */
  private readonly latestIncoming = new Map<string, string>();
  private lastSessionFp = "";
  private timers: ReturnType<typeof setTimeout>[] = [];
  private intervals: ReturnType<typeof setInterval>[] = [];
  private loginStartedAt = 0;
  private awaitingLoginDecision = false;

  constructor(deps: WorkerDeps) {
    this.deps = deps;
    this.io = deps.io;
    this.log = deps.log;
    this.now = deps.now ?? (() => Date.now());
    this.sleep = deps.sleep ?? defaultSleep;
  }

  handle(msg: ParentToWorker): void {
    switch (msg.type) {
      case "init":
        void this.onInit(msg.init);
        break;
      case "cmd":
        void this.onCommand(msg.id, msg.command);
        break;
      case "loginAccepted":
        void this.onLoginAccepted(msg.connectionId);
        break;
      case "loginRejected":
        this.finish(EXIT_OK);
        break;
      case "stop":
        this.finish(EXIT_OK);
        break;
    }
  }

  // ---- lifecycle ---------------------------------------------------------

  private finish(code: number): void {
    if (this.exiting) return;
    this.exiting = true;
    this.ready = false;
    for (const t of this.timers) clearTimeout(t);
    for (const i of this.intervals) clearInterval(i);
    try {
      this.session?.stop();
    } catch {
      // ignore
    }
    // Small delay so a clean WebSocket close frame and pending IPC messages get out.
    setTimeout(() => this.io.exit(code), this.deps.exitDelayMs ?? 200);
  }

  private emitState(state: ListenerState, code: number | null = null, detail: string | null = null): void {
    this.io.send({ type: "event", event: { type: "state", state, code, detail, ts: this.now() } });
  }

  private async onInit(init: WorkerInit): Promise<void> {
    if (this.init) return;
    this.init = init;
    this.proxyUrl = init.proxyUrl;
    registerSecret(init.proxyUrl);
    try {
      this.session = await this.deps.createSession(init);
    } catch (err) {
      // e.g. invalid proxy URL
      this.log.error("cannot create session", errFields(err));
      if (init.role === "login") {
        this.io.send({ type: "loginProgress", state: "error", error: "invalid_config" });
      } else {
        this.emitState("proxy_failing", null, "invalid proxy configuration");
      }
      this.finish(EXIT_OK);
      return;
    }
    if (init.role === "login") {
      this.log = this.log.child({ loginId: init.loginId, role: "login" });
      registerSecret(init.userAgent);
      await this.runQrLogin(init.userAgent);
    } else {
      this.connectionId = init.connectionId;
      this.log = this.log.child({ connectionId: init.connectionId, role: "account" });
      registerSecret(init.session.imei);
      registerSecret(init.session.userAgent);
      await this.runAccount(init.session);
    }
  }

  // ---- QR login ----------------------------------------------------------

  private async runQrLogin(userAgent: string): Promise<void> {
    const session = this.session!;
    this.loginStartedAt = this.now();
    try {
      const outcome = await session.loginQR(userAgent, {
        onQr: (png) => this.io.send({ type: "loginProgress", state: "qr", qrPng: png }),
        onScanned: () => this.io.send({ type: "loginProgress", state: "scanned" }),
        onExpired: () => {
          const left = this.loginStartedAt + LOGIN_TIMEOUT_MS - this.now();
          const retry = left > QR_RETRY_MIN_REMAINING_MS;
          this.log.info(retry ? "qr expired, generating a new one" : "qr expired, giving up");
          return retry;
        },
      });
      if (this.exiting) return;
      registerSecret(outcome.session.imei);
      this.lastSessionFp = sessionFingerprint(outcome.session);
      this.awaitingLoginDecision = true;
      this.io.send({
        type: "loginResult",
        uid: outcome.uid,
        displayName: outcome.displayName,
        avatarUrl: outcome.avatarUrl,
        session: outcome.session,
      });
      this.log.info("qr login completed, waiting for API decision");
    } catch (err) {
      if (this.exiting) return;
      if (err instanceof QrDeclinedError) {
        this.io.send({ type: "loginProgress", state: "declined" });
      } else if (err instanceof QrExpiredError) {
        this.io.send({ type: "loginProgress", state: "expired" });
      } else {
        const label = await this.loginFailureLabel(err);
        this.log.warn("qr login failed", { ...errFields(err), code: label });
        this.io.send({ type: "loginProgress", state: "error", error: label });
      }
      this.finish(EXIT_OK);
    }
  }

  private async loginFailureLabel(err: unknown): Promise<string> {
    if (!isNetworkError(err)) return zaloErrorCode(err) !== null ? "zalo_error" : "login_failed";
    if (this.proxyUrl) {
      const r = await this.runProxyTest(this.proxyUrl);
      if (!r.ok) return "proxy_failing";
    }
    return "network_error";
  }

  private async onLoginAccepted(connectionId: string): Promise<void> {
    if (!this.awaitingLoginDecision || this.exiting) return;
    this.awaitingLoginDecision = false;
    this.connectionId = connectionId;
    this.log = this.deps.log.child({ connectionId, role: "account" });
    this.log.info("login accepted, starting listener");
    await this.goLive();
  }

  // ---- account -----------------------------------------------------------

  private async runAccount(stored: ZaloSessionData): Promise<void> {
    const session = this.session!;
    this.lastSessionFp = sessionFingerprint(stored);
    try {
      await session.loginWithSession(stored);
    } catch (err) {
      if (this.exiting) return;
      if (err instanceof SessionExpiredError) {
        this.log.warn("stored session refused by Zalo");
        this.emitState("session_expired", null, "login with stored session failed");
        this.finish(EXIT_OK);
        return;
      }
      this.log.warn("login with stored session failed (network)", errFields(err));
      if (this.proxyUrl) {
        const r = await this.runProxyTest(this.proxyUrl);
        if (!r.ok) {
          this.emitState("proxy_failing", null, r.error ?? "proxy test failed");
          this.finish(EXIT_OK);
          return;
        }
      }
      this.emitState("reconnecting", null, "login network error");
      this.finish(EXIT_CRASH);
      return;
    }
    await this.goLive();
  }

  /** Logged in: report ready, load friends, start the listener and the 6-hourly session check. */
  private async goLive(): Promise<void> {
    if (this.exiting) return;
    const session = this.session!;
    this.ready = true;
    this.io.send({ type: "ready" });
    await this.refreshFriends();
    if (this.exiting) return;
    this.reportSessionIfChanged();
    session.startListener({
      onConnected: () => {
        this.log.info("listener connected");
        this.emitState("connected");
        this.reportSessionIfChanged();
      },
      onDisconnected: (code) => this.onDisconnected(code),
      onClosed: (code) => void this.onClosed(code),
      onError: (err) => this.log.debug("listener error", errFields(err)),
      onMessage: (m) => {
        const ev = translateMessage(m, { isFriend: (uid) => this.friends.has(uid), now: this.now });
        if (!ev) return;
        if (!ev.isSelf) this.rememberIncoming(ev.threadId, ev.msgId);
        this.io.send({ type: "event", event: ev });
      },
      onUndo: (u) => {
        const ev = translateUndo(u, this.now);
        if (ev) this.io.send({ type: "event", event: ev });
      },
      onFriendEvent: (f) => {
        const ev = translateFriendEvent(f, session.ownUid(), this.now);
        if (!ev) return;
        if (ev.event === "add") this.friends.add(ev.uid);
        if (ev.event === "remove") this.friends.delete(ev.uid);
        this.io.send({ type: "event", event: ev });
      },
    });
    this.intervals.push(
      setInterval(() => void this.periodicCheck(), this.deps.sessionCheckIntervalMs ?? SESSION_CHECK_INTERVAL_MS),
      setInterval(() => this.reportSessionIfChanged(), SESSION_PUSH_INTERVAL_MS),
    );
  }

  private async refreshFriends(): Promise<void> {
    try {
      const ids = await this.session!.listFriendIds();
      this.friends.clear();
      for (const id of ids) this.friends.add(id);
      this.log.info("friend list loaded", { count: ids.length });
    } catch (err) {
      this.log.warn("friend list failed, retrying later", errFields(err));
      this.timers.push(setTimeout(() => void this.refreshFriends(), FRIENDS_RETRY_MS));
    }
  }

  /** Every 6 h: push the cookie back to the API if Zalo refreshed it, and reload friends. */
  async periodicCheck(): Promise<void> {
    if (this.exiting || !this.session) return;
    this.reportSessionIfChanged();
    await this.refreshFriends();
  }

  /** Push the live cookie jar to the API when it differs from the last copy sent (zca-js updates it from every Set-Cookie). */
  private reportSessionIfChanged(): void {
    if (this.exiting || !this.session) return;
    const cur = this.session.currentSession();
    const fp = sessionFingerprint(cur);
    if (cur && fp !== this.lastSessionFp) {
      this.lastSessionFp = fp;
      this.io.send({ type: "session", session: cur });
      this.log.info("session changed, sent to API");
    }
  }

  private onDisconnected(code: number): void {
    if (this.exiting) return;
    if (code === 3000 || code === 3003) {
      // Zalo Web opened elsewhere / kicked: never fight for the session.
      this.log.warn("listener closed: opened elsewhere", { code });
      this.emitState("opened_elsewhere", code, code === 3000 ? "duplicate connection" : "kicked");
      this.finish(EXIT_OK);
      return;
    }
    this.log.warn("listener disconnected", { code });
    this.emitState("reconnecting", code, null);
  }

  private async onClosed(code: number): Promise<void> {
    if (this.exiting) return;
    // zca-js gave up retrying. Proxy dead → fail closed; otherwise let the supervisor restart us.
    if (this.proxyUrl) {
      const r = await this.runProxyTest(this.proxyUrl);
      if (this.exiting) return;
      if (!r.ok) {
        this.emitState("proxy_failing", code, r.error ?? "proxy test failed");
        this.finish(EXIT_OK);
        return;
      }
    }
    this.log.warn("listener closed for good, exiting for restart", { code });
    this.emitState("reconnecting", code, "listener closed");
    this.finish(EXIT_CRASH);
  }

  private async runProxyTest(proxyUrl: string): Promise<ProxyTestResult> {
    try {
      if (this.deps.proxyTest) return await this.deps.proxyTest(proxyUrl);
      return this.init?.mode === "fake" ? fakeTestProxy(proxyUrl) : await testProxy(proxyUrl);
    } catch {
      return { ok: false, error: "proxy_test_error" };
    }
  }

  // ---- commands ----------------------------------------------------------

  private reply(id: number, result: CommandResult): void {
    this.io.send({ type: "reply", id, result });
  }

  private async onCommand(id: number, command: WorkerCommand): Promise<void> {
    try {
      this.reply(id, await this.execute(command));
    } catch (err) {
      this.log.error("command failed unexpectedly", errFields(err));
      this.reply(id, { status: 503, body: { error: "unavailable" } });
    }
  }

  private notRunning(): CommandResult {
    return { status: 409, body: { error: "not_running" } };
  }

  private rememberIncoming(threadId: string, msgId: string): void {
    this.latestIncoming.delete(threadId); // re-insert → Map order = least recently active first
    this.latestIncoming.set(threadId, msgId);
    if (this.latestIncoming.size > LATEST_INCOMING_MAX) {
      const oldest = this.latestIncoming.keys().next().value;
      if (oldest !== undefined) this.latestIncoming.delete(oldest);
    }
  }

  /** Newer customer message than the one being answered? Unknown thread (e.g. after a restart) → not superseded, send. */
  private isSuperseded(threadId: string, replyTo: string | null | undefined): boolean {
    if (!replyTo) return false;
    const latest = this.latestIncoming.get(threadId);
    return latest !== undefined && latest !== replyTo;
  }

  private async execute(command: WorkerCommand): Promise<CommandResult> {
    if (command.cmd === "simulate") return this.simulate(command.args);
    if (!this.ready || this.exiting || !this.session) return this.notRunning();
    const session = this.session;
    switch (command.cmd) {
      case "send": {
        const { threadId, text, typingMs, replyTo } = command.args;
        try {
          if (this.isSuperseded(threadId, replyTo)) return SUPERSEDED;
          if (typingMs > 0) {
            try {
              await session.sendTyping(threadId);
            } catch (err) {
              this.log.debug("typing event failed", errFields(err));
            }
            await this.sleep(Math.min(typingMs, MAX_TYPING_MS));
            if (this.exiting) return this.notRunning();
            // The customer wrote again while we were "typing": this answer is stale, the API answers the new message instead.
            if (this.isSuperseded(threadId, replyTo)) return SUPERSEDED;
          }
          const msgId = await session.sendText(threadId, text);
          return { status: 200, body: { msgId } };
        } catch (err) {
          const result = errorToResult(err, text);
          this.log.warn("send failed", { ...errFields(err), status: result.status });
          return result;
        }
      }
      case "findUser": {
        try {
          const u = await session.findUser(command.args.phone);
          if (!u) return { status: 404, body: { error: "not_found" } };
          return { status: 200, body: u };
        } catch (err) {
          const code = zaloErrorCode(err);
          this.log.warn("find-user failed", { ...errFields(err) });
          // Zalo answers "no such user / not searchable" with an error code: treat as not found.
          if (code !== null) return { status: 404, body: { error: "not_found", code } };
          return errorToResult(err);
        }
      }
      case "friendRequest": {
        try {
          await session.sendFriendRequest(command.args.uid, command.args.message);
          return { status: 204 };
        } catch (err) {
          this.log.warn("friend-request failed", errFields(err));
          return errorToResult(err, command.args.message);
        }
      }
      case "undoFriendRequest": {
        try {
          await session.undoFriendRequest(command.args.uid);
          return { status: 204 };
        } catch (err) {
          this.log.warn("undo-friend-request failed", errFields(err));
          return errorToResult(err);
        }
      }
    }
  }

  private simulate(sim: FakeSimulation): CommandResult {
    const session = this.session;
    if (!session || !isFakeControls(session) || this.init?.mode !== "fake") {
      return { status: 404, body: { error: "not_found" } };
    }
    switch (sim.action) {
      case "scan_qr":
      case "decline_qr":
      case "expire_qr": {
        if (this.init.role !== "login") return { status: 404, body: { error: "not_found" } };
        const action = sim.action === "scan_qr" ? "scan" : sim.action === "decline_qr" ? "decline" : "expire";
        const uid = sim.uid ?? defaultFakeUid(this.init.loginId);
        const ok = session.simulateQr(action, uid, sim.displayName);
        return ok ? { status: 204 } : { status: 409, body: { error: "not_waiting" } };
      }
      case "session_expired":
        this.emitState("session_expired", null, "simulated");
        this.finish(EXIT_OK);
        return { status: 204 };
      case "proxy_fail":
        this.emitState("proxy_failing", null, "simulated");
        this.finish(EXIT_OK);
        return { status: 204 };
      case "incoming":
        if (sim.isFriend === true && sim.fromUid) this.friends.add(sim.fromUid);
        if (sim.isFriend === false && sim.fromUid) this.friends.delete(sim.fromUid);
        session.simulate(sim);
        return { status: 204 };
      case "self_message":
      case "friend":
      case "close":
        session.simulate(sim);
        return { status: 204 };
      default:
        return { status: 400, body: { error: "unknown_action" } };
    }
  }
}

// ---------------------------------------------------------------------------
// Child-process entry point
// ---------------------------------------------------------------------------

async function createSessionFor(init: WorkerInit): Promise<ZaloSession> {
  if (init.mode === "fake") {
    const { FakeZaloSession } = await import("./session/fake-session.ts");
    return new FakeZaloSession();
  }
  const { RealZaloSession } = await import("./session/real-session.ts");
  return new RealZaloSession(init.proxyUrl);
}

function runAsChild(): void {
  const level = (process.env.REMI_LOG_LEVEL ?? "info").toLowerCase() as LogLevel;
  const log = createLogger({ level, base: { pid: process.pid } });
  const core = new WorkerCore({
    io: {
      send: (msg) => {
        if (process.connected && process.send) process.send(msg);
      },
      exit: (code) => process.exit(code),
    },
    log,
    createSession: createSessionFor,
  });
  process.on("message", (msg) => core.handle(msg as ParentToWorker));
  // Parent gone → nothing left to serve.
  process.on("disconnect", () => process.exit(EXIT_OK));
  process.on("uncaughtException", (err) => {
    log.error("worker uncaught exception", errFields(err));
    process.exit(EXIT_CRASH);
  });
  process.on("unhandledRejection", (err) => {
    log.error("worker unhandled rejection", errFields(err));
    process.exit(EXIT_CRASH);
  });
}

if (import.meta.main && typeof process.send === "function") {
  runAsChild();
}
