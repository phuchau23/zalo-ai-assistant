// Supervisor (parent process): owns the worker registry, desired-state reconciliation (/sync + lease),
// QR login orchestration, command routing and crash restarts. Never holds zca-js objects; session JSON
// is only kept to hand it to a (re)started worker.
import { RestartBackoff } from "./backoff.ts";
import type { LoginResultOutcome, SyncResponse } from "./api-client.ts";
import type { Logger } from "./log.ts";
import type { SentCache } from "./sent-cache.ts";
import {
  TERMINAL_STATES,
  type CommandResult,
  type FakeSimulation,
  type LoginProgressState,
  type ParentToWorker,
  type PersonalEvent,
  type SendArgs,
  type WorkerCommand,
  type WorkerInit,
  type WorkerToParent,
  type ZaloSessionData,
} from "./types.ts";

export interface WorkerHandle {
  readonly pid: number | undefined;
  send(msg: ParentToWorker): void;
  onMessage(cb: (msg: WorkerToParent) => void): void;
  onExit(cb: (code: number | null, signal: string | null) => void): void;
  kill(): void;
}

export type SpawnWorker = (label: string) => WorkerHandle;

export type SupervisorApi = {
  sync(instance: string, running: { connectionId: string; state: string }[]): Promise<SyncResponse>;
  loginProgress(body: {
    loginId: string;
    state: LoginProgressState;
    qrPng?: string;
    error?: string;
  }): Promise<"ok" | "gone" | "failed">;
  loginResult(body: {
    loginId: string;
    uid: string;
    displayName: string;
    avatarUrl?: string | null;
    session: ZaloSessionData;
  }): Promise<LoginResultOutcome>;
  postSession(connectionId: string, session: ZaloSessionData): Promise<boolean>;
};

export type AccountConfig = { session: ZaloSessionData; proxyUrl: string | null };

export type SupervisorOptions = {
  instanceId: string;
  mode: "real" | "fake";
  api: SupervisorApi;
  spawn: SpawnWorker;
  pushEvent: (connectionId: string, event: PersonalEvent) => void;
  sentCache: SentCache;
  log: Logger;
  now?: () => number;
  loginTimeoutMs?: number;
  stopGraceMs?: number;
  /** No successful lease renewal for this long → stop every nick (fail closed). */
  leaseTtlMs?: number;
};

type WorkerRec = {
  handle: WorkerHandle;
  kind: "login" | "account";
  key: string;
  pending: Map<number, { resolve: (r: CommandResult) => void; timer: ReturnType<typeof setTimeout> }>;
  exited: boolean;
  exitWaiters: (() => void)[];
  stopping: boolean;
};

type AccountStatus = "starting" | "connected" | "reconnecting" | "backoff";

type AccountEntry = {
  connectionId: string;
  config: AccountConfig;
  worker: WorkerRec | null;
  status: AccountStatus;
  ready: boolean;
  healthySince: number | null;
  terminal: string | null;
  backoff: RestartBackoff;
  restartTimer: ReturnType<typeof setTimeout> | null;
};

type LoginEntry = {
  loginId: string;
  rec: WorkerRec;
  proxyUrl: string | null;
  finished: boolean;
  deadline: ReturnType<typeof setTimeout>;
  chain: Promise<unknown>;
};

const NOT_RUNNING: CommandResult = { status: 409, body: { error: "not_running" } };
const UNAVAILABLE: CommandResult = { status: 503, body: { error: "unavailable" } };
const NOT_FOUND: CommandResult = { status: 404, body: { error: "not_found" } };

export const DEFAULT_LOGIN_TIMEOUT_MS = 5 * 60_000;
export const DEFAULT_LEASE_TTL_MS = 90_000;

export class Supervisor {
  private readonly opts: SupervisorOptions;
  private readonly log: Logger;
  private readonly now: () => number;
  private readonly accounts = new Map<string, AccountEntry>();
  private readonly logins = new Map<string, LoginEntry>();
  /** Accounts that ended in a terminal state; not restarted by /sync until the API changes its mind. */
  private readonly terminalMarks = new Map<string, { proxyUrl: string | null }>();
  /**
   * Stops still in flight. A nick must never have two workers at once (Zalo would kick one with 3000),
   * so a start that arrives while the old worker is closing waits for it.
   */
  private readonly stopping = new Map<string, Promise<void>>();
  /** QR login being re-keyed onto this connectionId: no other start may race it. */
  private readonly rekeying = new Set<string>();
  private cmdSeq = 0;
  private syncing = false;
  private lastLeaseOkAt: number | null = null;
  private shuttingDown = false;

  constructor(options: SupervisorOptions) {
    this.opts = options;
    this.log = options.log;
    this.now = options.now ?? (() => Date.now());
  }

  // ---- introspection -----------------------------------------------------

  health(): {
    instance: string;
    mode: string;
    accounts: { connectionId: string; state: string }[];
    logins: string[];
  } {
    return {
      instance: this.opts.instanceId,
      mode: this.opts.mode,
      accounts: [...this.accounts.values()].map((e) => ({ connectionId: e.connectionId, state: e.status })),
      logins: [...this.logins.keys()],
    };
  }

  /** `running` list for /sync: only nicks with a live worker; contract states only. */
  runningForSync(): { connectionId: string; state: string }[] {
    return [...this.accounts.values()]
      .filter((e) => e.worker !== null)
      .map((e) => ({ connectionId: e.connectionId, state: e.status === "connected" ? "connected" : "reconnecting" }));
  }

  isTerminal(connectionId: string): boolean {
    return this.terminalMarks.has(connectionId);
  }

  // ---- workers -----------------------------------------------------------

  private spawnRec(kind: "login" | "account", key: string, init: WorkerInit): WorkerRec {
    const handle = this.opts.spawn(`${kind}:${key}`);
    const rec: WorkerRec = {
      handle,
      kind,
      key,
      pending: new Map(),
      exited: false,
      exitWaiters: [],
      stopping: false,
    };
    handle.onMessage((msg) => this.onWorkerMessage(rec, msg));
    handle.onExit((code, signal) => this.onWorkerExit(rec, code, signal));
    handle.send({ type: "init", init });
    this.log.info("worker started", {
      role: kind,
      pid: handle.pid,
      ...(kind === "login" ? { loginId: key } : { connectionId: key }),
    });
    return rec;
  }

  private stopRec(rec: WorkerRec): Promise<void> {
    if (rec.exited) return Promise.resolve();
    rec.stopping = true;
    this.failPending(rec, NOT_RUNNING);
    const done = new Promise<void>((resolve) => rec.exitWaiters.push(resolve));
    rec.handle.send({ type: "stop" });
    const killTimer = setTimeout(() => {
      if (!rec.exited) rec.handle.kill();
    }, this.opts.stopGraceMs ?? 5000);
    return done.finally(() => clearTimeout(killTimer));
  }

  private failPending(rec: WorkerRec, result: CommandResult): void {
    for (const [id, p] of rec.pending) {
      clearTimeout(p.timer);
      rec.pending.delete(id);
      p.resolve(result);
    }
  }

  private sendCommand(rec: WorkerRec, command: WorkerCommand, timeoutMs: number): Promise<CommandResult> {
    if (rec.exited || rec.stopping) return Promise.resolve(NOT_RUNNING);
    const id = ++this.cmdSeq;
    return new Promise<CommandResult>((resolve) => {
      const timer = setTimeout(() => {
        rec.pending.delete(id);
        resolve(UNAVAILABLE);
      }, timeoutMs);
      rec.pending.set(id, { resolve, timer });
      rec.handle.send({ type: "cmd", id, command });
    });
  }

  private onWorkerMessage(rec: WorkerRec, msg: WorkerToParent): void {
    if (msg.type === "reply") {
      const p = rec.pending.get(msg.id);
      if (p) {
        clearTimeout(p.timer);
        rec.pending.delete(msg.id);
        p.resolve(msg.result);
      }
      return;
    }
    if (rec.stopping) return;
    if (rec.kind === "login") this.onLoginMessage(rec, msg);
    else this.onAccountMessage(rec, msg);
  }

  private onWorkerExit(rec: WorkerRec, code: number | null, signal: string | null): void {
    rec.exited = true;
    this.failPending(rec, UNAVAILABLE);
    for (const w of rec.exitWaiters.splice(0)) w();

    if (rec.kind === "login") {
      const entry = this.logins.get(rec.key);
      if (!entry || entry.rec !== rec) return;
      this.logins.delete(rec.key);
      clearTimeout(entry.deadline);
      if (!entry.finished && !rec.stopping) {
        this.log.warn("login worker exited unexpectedly", { loginId: rec.key, exitCode: code, signal });
        this.progress(entry, { state: "error", error: "worker_exited" });
      }
      return;
    }

    const entry = this.accounts.get(rec.key);
    if (!entry || entry.worker !== rec) return;
    entry.worker = null;
    entry.ready = false;
    if (rec.stopping || this.shuttingDown) return;

    if (entry.terminal !== null) {
      this.accounts.delete(entry.connectionId);
      this.terminalMarks.set(entry.connectionId, { proxyUrl: entry.config.proxyUrl });
      this.log.warn("account stopped in terminal state, waiting for API", {
        connectionId: entry.connectionId,
        state: entry.terminal,
      });
      return;
    }

    const delay = entry.backoff.nextDelay(entry.healthySince, this.now());
    entry.status = "backoff";
    entry.healthySince = null;
    this.log.error("account worker crashed, scheduling restart", {
      connectionId: entry.connectionId,
      exitCode: code,
      signal,
      delayMs: delay,
      attempt: entry.backoff.failureCount,
    });
    this.opts.pushEvent(entry.connectionId, {
      type: "state",
      state: "reconnecting",
      code: null,
      detail: `worker exited, restart in ${Math.round(delay / 1000)}s`,
      ts: this.now(),
    });
    entry.restartTimer = setTimeout(() => {
      entry.restartTimer = null;
      if (this.accounts.get(entry.connectionId) === entry && entry.worker === null && !this.shuttingDown) {
        this.startWorker(entry);
      }
    }, delay);
  }

  // ---- accounts ----------------------------------------------------------

  private startWorker(entry: AccountEntry): void {
    entry.status = "starting";
    entry.ready = false;
    entry.terminal = null;
    entry.worker = this.spawnRec("account", entry.connectionId, {
      role: "account",
      connectionId: entry.connectionId,
      session: entry.config.session,
      proxyUrl: entry.config.proxyUrl,
      mode: this.opts.mode,
    });
  }

  /**
   * Start a nick. `explicit` = POST /accounts/{id}/start from the API (overrides terminal marks and
   * pending backoff); otherwise called by /sync reconciliation.
   * Returns "started" | "running" | "skipped".
   */
  startAccount(connectionId: string, config: AccountConfig, explicit: boolean): "started" | "running" | "skipped" {
    if (this.shuttingDown || this.rekeying.has(connectionId)) return "skipped";
    const pendingStop = this.stopping.get(connectionId);
    if (pendingStop) {
      void pendingStop.then(() => {
        this.startAccount(connectionId, config, explicit);
      });
      return "started";
    }
    const existing = this.accounts.get(connectionId);
    if (existing) {
      if (existing.worker) return "running";
      existing.config = config;
      if (existing.restartTimer) {
        if (!explicit) return "skipped";
        clearTimeout(existing.restartTimer);
        existing.restartTimer = null;
        existing.backoff.reset();
      }
      this.startWorker(existing);
      return "started";
    }
    const mark = this.terminalMarks.get(connectionId);
    if (mark) {
      if (!explicit && mark.proxyUrl === config.proxyUrl) return "skipped";
      this.terminalMarks.delete(connectionId);
    }
    const entry: AccountEntry = {
      connectionId,
      config,
      worker: null,
      status: "starting",
      ready: false,
      healthySince: null,
      terminal: null,
      backoff: new RestartBackoff(),
      restartTimer: null,
    };
    this.accounts.set(connectionId, entry);
    this.startWorker(entry);
    return "started";
  }

  /** Stop a nick: listener down, session dropped from memory. Resolves when the worker has exited. */
  async stopAccount(
    connectionId: string,
    options: { emitStopped: boolean; detail?: string } = { emitStopped: true },
  ): Promise<void> {
    const entry = this.accounts.get(connectionId);
    if (!entry) {
      // Already stopping (e.g. a second /stop): answer once the worker is really gone.
      await this.stopping.get(connectionId);
      return;
    }
    this.accounts.delete(connectionId);
    if (entry.restartTimer) clearTimeout(entry.restartTimer);
    entry.restartTimer = null;
    const rec = entry.worker;
    entry.worker = null;
    entry.ready = false;
    if (options.emitStopped) {
      this.opts.pushEvent(connectionId, {
        type: "state",
        state: "stopped",
        code: null,
        detail: options.detail ?? null,
        ts: this.now(),
      });
    }
    this.log.info("stopping account", { connectionId });
    if (!rec) return;
    const done = this.stopRec(rec);
    this.stopping.set(connectionId, done);
    try {
      await done;
    } finally {
      if (this.stopping.get(connectionId) === done) this.stopping.delete(connectionId);
    }
  }

  /** API answered 404 for this connection's events: it no longer exists. */
  handleGone(connectionId: string): void {
    this.terminalMarks.delete(connectionId);
    void this.stopAccount(connectionId, { emitStopped: false });
  }

  private onAccountMessage(rec: WorkerRec, msg: WorkerToParent): void {
    const entry = this.accounts.get(rec.key);
    if (!entry || entry.worker !== rec) return; // stale worker
    switch (msg.type) {
      case "ready":
        entry.ready = true;
        break;
      case "event": {
        const ev = msg.event;
        if (ev.type === "state") {
          if (ev.state === "connected") {
            entry.status = "connected";
            entry.healthySince = this.now();
          } else if (ev.state === "reconnecting") {
            entry.status = "reconnecting";
          } else if (TERMINAL_STATES.has(ev.state)) {
            entry.terminal = ev.state;
            entry.ready = false;
          }
          this.log.info("account state", { connectionId: entry.connectionId, state: ev.state, code: ev.code });
        }
        this.opts.pushEvent(entry.connectionId, ev);
        break;
      }
      case "session":
        entry.config = { ...entry.config, session: msg.session };
        void this.opts.api.postSession(entry.connectionId, msg.session);
        break;
      default:
        break;
    }
  }

  async command(
    connectionId: string,
    command: WorkerCommand,
    timeoutMs: number,
    requireReady = true,
  ): Promise<CommandResult> {
    const entry = this.accounts.get(connectionId);
    if (!entry || !entry.worker || (requireReady && !entry.ready)) return NOT_RUNNING;
    return this.sendCommand(entry.worker, command, timeoutMs);
  }

  /** CONTRACT §5: idempotent by messageId for 1 h; concurrent duplicates share one send. */
  async send(connectionId: string, messageId: string, args: SendArgs): Promise<CommandResult> {
    const cache = this.opts.sentCache;
    const cached = cache.get(connectionId, messageId);
    if (cached !== undefined) return { status: 200, body: { msgId: cached } };
    return cache.once(connectionId, messageId, async () => {
      const again = cache.get(connectionId, messageId);
      if (again !== undefined) return { status: 200, body: { msgId: again } };
      const timeout = Math.min(Math.max(args.typingMs, 0), 15_000) + 60_000;
      const r = await this.command(connectionId, { cmd: "send", args }, timeout);
      if (r.status === 200) {
        const msgId = (r.body as { msgId?: unknown } | undefined)?.msgId;
        if (typeof msgId === "string" && msgId.length > 0) cache.remember(connectionId, messageId, msgId);
      }
      return r;
    });
  }

  // ---- QR login ----------------------------------------------------------

  startLogin(loginId: string, userAgent: string, proxyUrl: string | null): "started" | "running" {
    if (this.logins.has(loginId)) return "running";
    const rec = this.spawnRec("login", loginId, { role: "login", loginId, userAgent, proxyUrl, mode: this.opts.mode });
    const entry: LoginEntry = {
      loginId,
      rec,
      proxyUrl,
      finished: false,
      chain: Promise.resolve(),
      deadline: setTimeout(() => this.onLoginTimeout(loginId), this.opts.loginTimeoutMs ?? DEFAULT_LOGIN_TIMEOUT_MS),
    };
    this.logins.set(loginId, entry);
    return "started";
  }

  async cancelLogin(loginId: string): Promise<void> {
    const entry = this.logins.get(loginId);
    if (!entry) return;
    this.logins.delete(loginId);
    clearTimeout(entry.deadline);
    entry.finished = true;
    this.log.info("login cancelled", { loginId });
    await this.stopRec(entry.rec);
  }

  async simulateLogin(loginId: string, sim: FakeSimulation): Promise<CommandResult> {
    const entry = this.logins.get(loginId);
    if (!entry) return NOT_FOUND;
    return this.sendCommand(entry.rec, { cmd: "simulate", args: sim }, 5000);
  }

  async simulateAccount(connectionId: string, sim: FakeSimulation): Promise<CommandResult> {
    const entry = this.accounts.get(connectionId);
    if (!entry || !entry.worker) return NOT_FOUND;
    return this.sendCommand(entry.worker, { cmd: "simulate", args: sim }, 5000);
  }

  private onLoginTimeout(loginId: string): void {
    const entry = this.logins.get(loginId);
    if (!entry || entry.finished) return;
    entry.finished = true;
    this.log.info("login timed out", { loginId });
    this.progress(entry, { state: "expired" });
    void this.cancelLogin(loginId);
  }

  /** Post login progress in order (chained per login). */
  private progress(entry: LoginEntry, p: { state: LoginProgressState; qrPng?: string; error?: string }): void {
    this.chain(entry, async () => {
      const r = await this.opts.api.loginProgress({ loginId: entry.loginId, ...p });
      if (r === "gone" && this.logins.get(entry.loginId) === entry) {
        this.log.info("login no longer exists at API, stopping", { loginId: entry.loginId });
        entry.finished = true;
        await this.cancelLogin(entry.loginId);
      }
    });
  }

  /** Run login side effects one after another; a failure never blocks the next step. */
  private chain(entry: LoginEntry, fn: () => Promise<void>): void {
    entry.chain = entry.chain.then(fn).catch(() => {
      this.log.error("login step failed", { loginId: entry.loginId });
    });
  }

  private onLoginMessage(rec: WorkerRec, msg: WorkerToParent): void {
    const entry = this.logins.get(rec.key);
    if (!entry || entry.rec !== rec) return;
    if (msg.type === "loginProgress") {
      if (entry.finished) return;
      if (msg.state === "expired" || msg.state === "declined" || msg.state === "error") entry.finished = true;
      this.progress(entry, { state: msg.state, qrPng: msg.qrPng, error: msg.error });
      return;
    }
    if (msg.type === "loginResult") {
      if (entry.finished) return;
      entry.finished = true;
      this.chain(entry, async () => {
        const outcome = await this.opts.api.loginResult({
          loginId: entry.loginId,
          uid: msg.uid,
          displayName: msg.displayName,
          avatarUrl: msg.avatarUrl,
          session: msg.session,
        });
        await this.onLoginOutcome(entry, outcome, msg.session);
      });
    }
  }

  private async onLoginOutcome(entry: LoginEntry, outcome: LoginResultOutcome, session: ZaloSessionData): Promise<void> {
    if (this.logins.get(entry.loginId) !== entry) return; // cancelled meanwhile
    if (outcome.kind !== "ok") {
      this.log.warn("login result not accepted", {
        loginId: entry.loginId,
        state: outcome.kind,
        code: outcome.kind === "conflict" ? outcome.error : outcome.kind === "failed" ? (outcome.status ?? null) : null,
      });
      this.logins.delete(entry.loginId);
      clearTimeout(entry.deadline);
      entry.rec.stopping = true;
      entry.rec.handle.send({ type: "loginRejected" });
      setTimeout(() => {
        if (!entry.rec.exited) entry.rec.handle.kill();
      }, this.opts.stopGraceMs ?? 5000);
      return;
    }
    const connectionId = outcome.connectionId;
    this.logins.delete(entry.loginId);
    clearTimeout(entry.deadline);

    // Re-auth of an existing nick: the old worker must be gone before the new session goes live.
    // Block other starts while we wait (a /sync could otherwise spawn a second worker meanwhile).
    this.rekeying.add(connectionId);
    try {
      for (;;) {
        if (this.accounts.has(connectionId)) await this.stopAccount(connectionId, { emitStopped: false });
        else if (this.stopping.has(connectionId)) await this.stopping.get(connectionId);
        else break;
      }
    } finally {
      this.rekeying.delete(connectionId);
    }
    this.terminalMarks.delete(connectionId);

    const rec = entry.rec;
    rec.kind = "account";
    rec.key = connectionId;
    const account: AccountEntry = {
      connectionId,
      config: { session, proxyUrl: entry.proxyUrl },
      worker: null,
      status: "starting",
      ready: false,
      healthySince: null,
      terminal: null,
      backoff: new RestartBackoff(),
      restartTimer: null,
    };
    this.accounts.set(connectionId, account);
    if (rec.exited || this.shuttingDown) {
      if (!this.shuttingDown) this.startWorker(account);
      return;
    }
    account.worker = rec;
    this.log.info("login accepted, worker re-keyed", { loginId: entry.loginId, connectionId });
    rec.handle.send({ type: "loginAccepted", connectionId });
  }

  // ---- desired state (/sync) ---------------------------------------------

  async reconcile(resp: SyncResponse): Promise<void> {
    if (!resp.leaseGranted) {
      this.lastLeaseOkAt = null;
      if (this.accounts.size > 0 || this.logins.size > 0) {
        this.log.warn("lease not granted, stopping everything");
      }
      await this.stopAll("lease not granted");
      return;
    }
    this.lastLeaseOkAt = this.now();
    const desired = new Map(resp.accounts.map((a) => [a.connectionId, a]));
    const work: Promise<unknown>[] = [];

    for (const id of [...this.accounts.keys()]) {
      if (!desired.has(id)) work.push(this.stopAccount(id, { emitStopped: true, detail: "not in desired state" }));
    }
    for (const id of [...this.terminalMarks.keys()]) {
      if (!desired.has(id)) this.terminalMarks.delete(id);
    }
    for (const a of desired.values()) {
      const entry = this.accounts.get(a.connectionId);
      const config: AccountConfig = { session: a.session, proxyUrl: a.proxyUrl };
      if (!entry) {
        this.startAccount(a.connectionId, config, false);
        continue;
      }
      if (entry.config.proxyUrl !== a.proxyUrl) {
        // Proxy changed: stop, wait for exit, start again through the new proxy.
        this.log.info("proxy changed, restarting account", { connectionId: a.connectionId });
        work.push(
          this.stopAccount(a.connectionId, { emitStopped: false }).then(() => {
            this.startAccount(a.connectionId, config, true);
          }),
        );
        continue;
      }
      // A live worker keeps its own (newest) cookie; a nick waiting for restart takes the API's copy.
      if (!entry.worker) entry.config = config;
    }
    await Promise.all(work);
  }

  async syncOnce(): Promise<void> {
    if (this.syncing || this.shuttingDown) return;
    this.syncing = true;
    try {
      const resp = await this.opts.api.sync(this.opts.instanceId, this.runningForSync());
      await this.reconcile(resp);
    } catch (err) {
      const ttl = this.opts.leaseTtlMs ?? DEFAULT_LEASE_TTL_MS;
      const last = this.lastLeaseOkAt;
      this.log.warn("sync failed", { errName: err instanceof Error ? err.name : "Error" });
      if (last !== null && this.now() - last > ttl && this.accounts.size > 0) {
        this.log.error("lease could not be renewed in time, stopping all accounts", { durationMs: this.now() - last });
        this.lastLeaseOkAt = null;
        await this.stopAll("lease expired");
      }
    } finally {
      this.syncing = false;
    }
  }

  async stopAll(detail: string): Promise<void> {
    const work: Promise<unknown>[] = [];
    for (const id of [...this.accounts.keys()]) work.push(this.stopAccount(id, { emitStopped: true, detail }));
    for (const id of [...this.logins.keys()]) work.push(this.cancelLogin(id));
    await Promise.all(work);
  }

  async shutdown(): Promise<void> {
    this.shuttingDown = true;
    await this.stopAll("sidecar shutting down");
  }
}

