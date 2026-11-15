import { hostname } from "node:os";
import type { LogLevel } from "./log.ts";

export type Config = {
  port: number;
  host: string;
  apiUrl: string;
  sharedSecret: string;
  mode: "real" | "fake";
  instanceId: string;
  syncIntervalMs: number;
  logLevel: LogLevel;
};

export class ConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ConfigError";
  }
}

function intEnv(env: NodeJS.ProcessEnv, name: string, fallback: number, min: number): number {
  const raw = env[name];
  if (raw === undefined || raw === "") return fallback;
  const n = Number(raw);
  if (!Number.isInteger(n) || n < min) throw new ConfigError(`${name} must be an integer >= ${min}`);
  return n;
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const sharedSecret = env.REMI_SHARED_SECRET ?? "";
  if (sharedSecret.length < 32) {
    throw new ConfigError("REMI_SHARED_SECRET is required and must be at least 32 characters");
  }
  const mode = (env.REMI_MODE ?? "real").toLowerCase();
  if (mode !== "real" && mode !== "fake") throw new ConfigError("REMI_MODE must be real or fake");
  const logLevel = (env.REMI_LOG_LEVEL ?? "info").toLowerCase();
  if (!["debug", "info", "warn", "error"].includes(logLevel)) {
    throw new ConfigError("REMI_LOG_LEVEL must be debug, info, warn or error");
  }
  const apiUrl = (env.REMI_API_URL ?? "http://127.0.0.1:4001").replace(/\/+$/, "");
  try {
    const u = new URL(apiUrl);
    if (u.protocol !== "http:" && u.protocol !== "https:") throw new Error("bad scheme");
  } catch {
    throw new ConfigError("REMI_API_URL must be an http(s) URL");
  }
  return {
    port: intEnv(env, "REMI_PORT", 4100, 0), // 0 = any free port (tests)
    host: env.REMI_HOST && env.REMI_HOST.length > 0 ? env.REMI_HOST : "127.0.0.1",
    apiUrl,
    sharedSecret,
    mode,
    instanceId:
      env.REMI_INSTANCE_ID && env.REMI_INSTANCE_ID.length > 0
        ? env.REMI_INSTANCE_ID
        : `${hostname()}-${process.pid}`,
    syncIntervalMs: intEnv(env, "REMI_SYNC_INTERVAL_MS", 60_000, 1000),
    logLevel: logLevel as LogLevel,
  };
}
