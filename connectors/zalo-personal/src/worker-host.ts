// Real WorkerHandle: one child process per nick / per QR login (child_process.fork + IPC).
import { fork } from "node:child_process";
import { fileURLToPath } from "node:url";
import { errFields, type Logger } from "./log.ts";
import type { SpawnWorker, WorkerHandle } from "./supervisor.ts";
import type { ParentToWorker, WorkerToParent } from "./types.ts";

export const WORKER_PATH = fileURLToPath(new URL("./worker.ts", import.meta.url));

export function forkSpawner(log: Logger, logLevel: string): SpawnWorker {
  return (_label: string): WorkerHandle => {
    // The child never needs the shared secret.
    const env: NodeJS.ProcessEnv = { ...process.env, REMI_LOG_LEVEL: logLevel };
    delete env.REMI_SHARED_SECRET;
    const child = fork(WORKER_PATH, [], {
      execArgv: [], // do not inherit --watch etc.
      env,
      stdio: ["ignore", "inherit", "inherit", "ipc"],
      serialization: "json",
    });
    child.on("error", (err) => log.warn("worker process error", { pid: child.pid, ...errFields(err) }));
    return {
      pid: child.pid,
      send(msg: ParentToWorker): void {
        if (!child.connected) return;
        try {
          child.send(msg, (err) => {
            if (err) log.debug("ipc send failed", { pid: child.pid, ...errFields(err) });
          });
        } catch (err) {
          log.debug("ipc send failed", { pid: child.pid, ...errFields(err) });
        }
      },
      onMessage(cb: (msg: WorkerToParent) => void): void {
        child.on("message", (m) => cb(m as WorkerToParent));
      },
      onExit(cb: (code: number | null, signal: string | null) => void): void {
        child.once("exit", (code, signal) => cb(code, signal));
      },
      kill(): void {
        if (child.exitCode === null && child.signalCode === null) child.kill();
      },
    };
  };
}
