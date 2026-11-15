// Entry point: wires config → logger → API client → event queue → supervisor → HTTP server → sync loop.
import { existsSync } from "node:fs";
import { createServer } from "node:http";
import { fileURLToPath } from "node:url";
import { ApiClient } from "./api-client.ts";
import { ConfigError, loadConfig, type Config } from "./config.ts";
import { EventQueue } from "./event-queue.ts";
import { createRequestHandler } from "./http-server.ts";
import { createLogger, errFields, registerSecret, type Logger } from "./log.ts";
import { fakeTestProxy, testProxy } from "./proxy.ts";
import { SentCache } from "./sent-cache.ts";
import { Supervisor, type SpawnWorker } from "./supervisor.ts";
import { forkSpawner } from "./worker-host.ts";

export type App = {
  supervisor: Supervisor;
  queue: EventQueue;
  close(): Promise<void>;
  port: number;
};

export async function startApp(
  config: Config,
  options: { log?: Logger; spawn?: SpawnWorker; fetchFn?: ConstructorParameters<typeof ApiClient>[0]["fetchFn"] } = {},
): Promise<App> {
  registerSecret(config.sharedSecret);
  const log = options.log ?? createLogger({ level: config.logLevel, base: { pid: process.pid } });
  const api = new ApiClient({ baseUrl: config.apiUrl, secret: config.sharedSecret, log, fetchFn: options.fetchFn });
  const sentCache = new SentCache();

  // The queue needs the supervisor (404 → stop nick) and vice versa: bind lazily.
  let supervisor: Supervisor | null = null;
  const queue = new EventQueue({
    post: (connectionId, events) => api.postEvents(connectionId, events),
    onGone: (connectionId) => supervisor?.handleGone(connectionId),
    isSentByUs: (connectionId, msgId) => sentCache.wasSentByUs(connectionId, msgId),
    log,
  });
  const sup = new Supervisor({
    instanceId: config.instanceId,
    mode: config.mode,
    api,
    spawn: options.spawn ?? forkSpawner(log, config.logLevel),
    pushEvent: (connectionId, event) => queue.push(connectionId, event),
    sentCache,
    log,
  });
  supervisor = sup;

  const server = createServer(
    createRequestHandler({
      secret: config.sharedSecret,
      mode: config.mode,
      supervisor: sup,
      log,
      testProxy: async (proxyUrl) => {
        registerSecret(proxyUrl);
        return config.mode === "fake" ? fakeTestProxy(proxyUrl) : testProxy(proxyUrl);
      },
    }),
  );
  server.requestTimeout = 120_000;
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(config.port, config.host, () => resolve());
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : config.port;
  log.info("sidecar listening", { host: config.host, port, mode: config.mode, instance: config.instanceId });

  void sup.syncOnce();
  const syncTimer = setInterval(() => void sup.syncOnce(), config.syncIntervalMs);
  const pruneTimer = setInterval(() => sentCache.prune(), 5 * 60_000);

  let closed = false;
  return {
    supervisor: sup,
    queue,
    port,
    async close() {
      if (closed) return;
      closed = true;
      clearInterval(syncTimer);
      clearInterval(pruneTimer);
      await sup.shutdown();
      // Give queued events (e.g. "stopped") a moment to reach the API.
      const deadline = Date.now() + 3000;
      while (!queue.isIdle() && Date.now() < deadline) await new Promise((r) => setTimeout(r, 100));
      queue.stop();
      await new Promise<void>((resolve) => server.close(() => resolve()));
      server.closeAllConnections();
    },
  };
}

async function main(): Promise<void> {
  const envFile = fileURLToPath(new URL("../.env", import.meta.url));
  if (existsSync(envFile)) process.loadEnvFile(envFile);

  let config: Config;
  try {
    config = loadConfig();
  } catch (err) {
    const msg = err instanceof ConfigError ? err.message : "invalid configuration";
    process.stderr.write(JSON.stringify({ ts: new Date().toISOString(), level: "error", msg }) + "\n");
    process.exit(1);
  }
  const log = createLogger({ level: config.logLevel, base: { pid: process.pid } });
  if (config.mode === "fake") log.warn("REMI_MODE=fake: no real Zalo connection (dev only)");

  const app = await startApp(config, { log });
  let stopping = false;
  const shutdown = (signal: string) => {
    if (stopping) return;
    stopping = true;
    log.info("shutting down", { signal });
    const force = setTimeout(() => process.exit(0), 10_000);
    force.unref();
    void app.close().finally(() => process.exit(0));
  };
  process.on("SIGINT", () => shutdown("SIGINT"));
  process.on("SIGTERM", () => shutdown("SIGTERM"));
  process.on("unhandledRejection", (err) => log.error("unhandled rejection", errFields(err)));
  process.on("uncaughtException", (err) => {
    log.error("uncaught exception", errFields(err));
    shutdown("uncaughtException");
  });
}

if (import.meta.main) {
  void main();
}
