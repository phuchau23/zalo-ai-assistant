import { createLogger, type Logger } from "../src/log.ts";
import type { SpawnWorker, SupervisorApi, WorkerHandle } from "../src/supervisor.ts";
import { WorkerCore, type WorkerDeps } from "../src/worker.ts";
import type { ZaloSession } from "../src/session/zalo-session.ts";
import type { ParentToWorker, PersonalEvent, WorkerInit, WorkerToParent } from "../src/types.ts";
import type { SyncResponse, LoginResultOutcome } from "../src/api-client.ts";

export function captureLogger(level: "debug" | "info" = "debug"): { log: Logger; lines: string[] } {
  const lines: string[] = [];
  return { log: createLogger({ level, sink: (l) => lines.push(l) }), lines };
}

export const silentLog: Logger = createLogger({ level: "error", sink: () => {} });

/** Mimics IPC: JSON round-trip, async delivery. */
const ipc = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;

export type InProcessWorker = {
  handle: WorkerHandle;
  core: WorkerCore;
  sentToWorker: ParentToWorker[];
  exited: boolean;
};

/** Spawner that runs WorkerCore in-process (no fork) — for supervisor + fake session flow tests. */
export function inProcessSpawner(options: {
  createSession: (init: WorkerInit) => Promise<ZaloSession>;
  log?: Logger;
  deps?: Partial<WorkerDeps>;
}): { spawn: SpawnWorker; workers: InProcessWorker[] } {
  const workers: InProcessWorker[] = [];
  let pid = 1000;
  const spawn: SpawnWorker = () => {
    const msgCbs: ((m: WorkerToParent) => void)[] = [];
    const exitCbs: ((code: number | null, signal: string | null) => void)[] = [];
    const rec = { exited: false } as InProcessWorker;
    const fireExit = (code: number | null, signal: string | null) => {
      if (rec.exited) return;
      rec.exited = true;
      queueMicrotask(() => exitCbs.forEach((cb) => cb(code, signal)));
    };
    const core = new WorkerCore({
      io: {
        send: (msg) => {
          if (rec.exited) return;
          const copy = ipc(msg);
          queueMicrotask(() => msgCbs.forEach((cb) => cb(copy)));
        },
        exit: (code) => fireExit(code, null),
      },
      log: options.log ?? silentLog,
      createSession: options.createSession,
      exitDelayMs: 0,
      ...options.deps,
    });
    rec.core = core;
    rec.sentToWorker = [];
    rec.handle = {
      pid: ++pid,
      send: (msg) => {
        if (rec.exited) return;
        rec.sentToWorker.push(msg);
        const copy = ipc(msg);
        queueMicrotask(() => core.handle(copy));
      },
      onMessage: (cb) => msgCbs.push(cb),
      onExit: (cb) => exitCbs.push(cb),
      kill: () => fireExit(null, "SIGTERM"),
    };
    workers.push(rec);
    return rec.handle;
  };
  return { spawn, workers };
}

/** Scriptable fake WorkerHandle for pure supervisor tests. */
export class StubWorker implements WorkerHandle {
  readonly pid: number;
  readonly received: ParentToWorker[] = [];
  private msgCbs: ((m: WorkerToParent) => void)[] = [];
  private exitCbs: ((code: number | null, signal: string | null) => void)[] = [];
  exited = false;
  /** Exit automatically when asked to stop. */
  autoExitOnStop = true;

  constructor(pid: number) {
    this.pid = pid;
  }
  send(msg: ParentToWorker): void {
    this.received.push(msg);
    if (msg.type === "stop" && this.autoExitOnStop) queueMicrotask(() => this.exit(0));
  }
  onMessage(cb: (m: WorkerToParent) => void): void {
    this.msgCbs.push(cb);
  }
  onExit(cb: (code: number | null, signal: string | null) => void): void {
    this.exitCbs.push(cb);
  }
  kill(): void {
    this.exit(null, "SIGTERM");
  }
  emit(msg: WorkerToParent): void {
    for (const cb of this.msgCbs) cb(msg);
  }
  exit(code: number | null, signal: string | null = null): void {
    if (this.exited) return;
    this.exited = true;
    for (const cb of this.exitCbs) cb(code, signal);
  }
  get init(): WorkerInit | undefined {
    const m = this.received.find((r) => r.type === "init");
    return m && m.type === "init" ? m.init : undefined;
  }
}

export function stubSpawner(): { spawn: SpawnWorker; workers: StubWorker[] } {
  const workers: StubWorker[] = [];
  let pid = 100;
  return {
    workers,
    spawn: () => {
      const w = new StubWorker(++pid);
      workers.push(w);
      return w;
    },
  };
}

export class MockApi implements SupervisorApi {
  syncResponses: (SyncResponse | Error)[] = [];
  syncCalls: { instance: string; running: { connectionId: string; state: string }[] }[] = [];
  progress: { loginId: string; state: string; qrPng?: string; error?: string }[] = [];
  progressAnswer: "ok" | "gone" | "failed" = "ok";
  loginResults: { loginId: string; uid: string; displayName: string }[] = [];
  loginOutcome: LoginResultOutcome = { kind: "ok", connectionId: "conn-new" };
  sessions: { connectionId: string; session: unknown }[] = [];

  async sync(instance: string, running: { connectionId: string; state: string }[]): Promise<SyncResponse> {
    this.syncCalls.push({ instance, running });
    const next = this.syncResponses.shift() ?? { leaseGranted: true, accounts: [] };
    if (next instanceof Error) throw next;
    return next;
  }
  async loginProgress(body: { loginId: string; state: string; qrPng?: string; error?: string }) {
    this.progress.push(body);
    return this.progressAnswer;
  }
  async loginResult(body: { loginId: string; uid: string; displayName: string }): Promise<LoginResultOutcome> {
    this.loginResults.push({ loginId: body.loginId, uid: body.uid, displayName: body.displayName });
    return this.loginOutcome;
  }
  async postSession(connectionId: string, session: unknown): Promise<boolean> {
    this.sessions.push({ connectionId, session });
    return true;
  }
}

export function eventsCollector(): {
  push: (connectionId: string, event: PersonalEvent) => void;
  events: { connectionId: string; event: PersonalEvent }[];
} {
  const events: { connectionId: string; event: PersonalEvent }[] = [];
  return { events, push: (connectionId, event) => events.push({ connectionId, event }) };
}

export const SESSION = { cookie: [{ key: "zpw_sek", value: "c00kie-SECRET-value" }], imei: "imei-SECRET-123", userAgent: "UA-SECRET/1.0" };

/** Flush queued microtasks/promises a few rounds. */
export async function flush(rounds = 10): Promise<void> {
  for (let i = 0; i < rounds; i++) await Promise.resolve();
}
