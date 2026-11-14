// Internal HTTP API (API → sidecar, CONTRACT §3). Every request must carry a valid HMAC signature.
import type { IncomingMessage, ServerResponse } from "node:http";
import { SIGNATURE_HEADER, verifySignature } from "./hmac.ts";
import type { Logger } from "./log.ts";
import { parseSession } from "./api-client.ts";
import type { ProxyTestResult } from "./proxy.ts";
import type { Supervisor } from "./supervisor.ts";
import type { CommandResult, FakeSimulation } from "./types.ts";

export const MAX_BODY_BYTES = 1024 * 1024;
const ID_RE = /^[A-Za-z0-9_-]{1,64}$/;
const MAX_TEXT = 20_000;

export type HttpDeps = {
  secret: string;
  mode: "real" | "fake";
  supervisor: Supervisor;
  log: Logger;
  testProxy: (proxyUrl: string) => Promise<ProxyTestResult>;
  now?: () => number;
};

class HttpError extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string) {
    super(code);
    this.status = status;
    this.code = code;
  }
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let tooBig = false;
    req.on("data", (c: Buffer) => {
      size += c.length;
      if (size > MAX_BODY_BYTES) {
        tooBig = true;
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => (tooBig ? reject(new HttpError(413, "payload_too_large")) : resolve(Buffer.concat(chunks).toString("utf8"))));
    req.on("error", reject);
  });
}

function send(res: ServerResponse, status: number, body?: unknown): void {
  if (status === 204 || body === undefined) {
    res.writeHead(status);
    res.end();
    return;
  }
  const json = JSON.stringify(body);
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", "content-length": Buffer.byteLength(json) });
  res.end(json);
}

function sendResult(res: ServerResponse, r: CommandResult): void {
  send(res, r.status, r.status === 204 ? undefined : r.body);
}

function obj(body: unknown): Record<string, unknown> {
  if (body === null || typeof body !== "object" || Array.isArray(body)) throw new HttpError(400, "bad_request");
  return body as Record<string, unknown>;
}

function str(o: Record<string, unknown>, key: string, opts: { max?: number; min?: number; re?: RegExp } = {}): string {
  const v = o[key];
  if (typeof v !== "string") throw new HttpError(400, "bad_request");
  if (v.length < (opts.min ?? 1) || v.length > (opts.max ?? 1000)) throw new HttpError(400, "bad_request");
  if (opts.re && !opts.re.test(v)) throw new HttpError(400, "bad_request");
  return v;
}

function optProxy(o: Record<string, unknown>): string | null {
  const v = o.proxyUrl;
  if (v === undefined || v === null || v === "") return null;
  if (typeof v !== "string" || v.length > 2048) throw new HttpError(400, "bad_request");
  try {
    const u = new URL(v);
    if (u.protocol !== "http:" && u.protocol !== "https:") throw new Error("scheme");
  } catch {
    throw new HttpError(400, "bad_request");
  }
  return v;
}

function parseJson(raw: string): unknown {
  if (raw.length === 0) return {};
  try {
    return JSON.parse(raw);
  } catch {
    throw new HttpError(400, "bad_json");
  }
}

export function createRequestHandler(deps: HttpDeps): (req: IncomingMessage, res: ServerResponse) => void {
  const { supervisor, log } = deps;

  async function route(method: string, path: string, raw: string, res: ServerResponse): Promise<void> {
    if (method === "GET" && path === "/health") return send(res, 200, supervisor.health());
    if (method !== "POST") throw new HttpError(404, "not_found");

    if (path === "/login-qr") {
      const b = obj(parseJson(raw));
      const loginId = str(b, "loginId", { max: 64, re: ID_RE });
      const userAgent = str(b, "userAgent", { max: 1024 });
      const proxyUrl = optProxy(b);
      supervisor.startLogin(loginId, userAgent, proxyUrl);
      return send(res, 202, { accepted: true });
    }
    let m = /^\/login-qr\/([^/]+)\/cancel$/.exec(path);
    if (m) {
      const loginId = decodeURIComponent(m[1]!);
      if (ID_RE.test(loginId)) await supervisor.cancelLogin(loginId);
      return send(res, 204);
    }
    if (path === "/proxies/test") {
      const b = obj(parseJson(raw));
      const proxyUrl = optProxy(b);
      if (!proxyUrl) throw new HttpError(400, "bad_request");
      return send(res, 200, await deps.testProxy(proxyUrl));
    }
    if (path === "/dev/simulate") {
      if (deps.mode !== "fake") throw new HttpError(404, "not_found");
      const b = obj(parseJson(raw));
      const sim = b as unknown as FakeSimulation;
      if (typeof sim.action !== "string") throw new HttpError(400, "bad_request");
      if (sim.action === "scan_qr" || sim.action === "decline_qr" || sim.action === "expire_qr") {
        const loginId = str(b, "loginId", { max: 64, re: ID_RE });
        return sendResult(res, await supervisor.simulateLogin(loginId, sim));
      }
      const connectionId = str(b, "connectionId", { max: 64, re: ID_RE });
      return sendResult(res, await supervisor.simulateAccount(connectionId, sim));
    }

    m = /^\/accounts\/([^/]+)\/([a-z-]+)$/.exec(path);
    if (!m) throw new HttpError(404, "not_found");
    const connectionId = decodeURIComponent(m[1]!);
    if (!ID_RE.test(connectionId)) throw new HttpError(404, "not_found");
    const action = m[2]!;

    switch (action) {
      case "start": {
        const b = obj(parseJson(raw));
        const session = parseSession(b.session);
        if (!session) throw new HttpError(400, "bad_request");
        const proxyUrl = optProxy(b);
        supervisor.startAccount(connectionId, { session, proxyUrl }, true);
        return send(res, 202, { accepted: true });
      }
      case "stop":
        await supervisor.stopAccount(connectionId, { emitStopped: true, detail: "stopped by API" });
        return send(res, 204);
      case "send": {
        const b = obj(parseJson(raw));
        const messageId = str(b, "messageId", { max: 64, re: ID_RE });
        const threadId = str(b, "threadId", { max: 128 });
        const text = str(b, "text", { max: MAX_TEXT });
        const typingRaw = b.typingMs ?? 0;
        if (typeof typingRaw !== "number" || !Number.isFinite(typingRaw) || typingRaw < 0) {
          throw new HttpError(400, "bad_request");
        }
        const replyTo = b.replyTo ?? null;
        if (replyTo !== null && (typeof replyTo !== "string" || replyTo.length === 0 || replyTo.length > 128)) {
          throw new HttpError(400, "bad_request");
        }
        return sendResult(res, await supervisor.send(connectionId, messageId, { threadId, text, typingMs: typingRaw, replyTo }));
      }
      case "find-user": {
        const b = obj(parseJson(raw));
        const phone = str(b, "phone", { max: 20, re: /^\+?\d{6,15}$/ });
        return sendResult(res, await supervisor.command(connectionId, { cmd: "findUser", args: { phone } }, 30_000));
      }
      case "friend-request": {
        const b = obj(parseJson(raw));
        const uid = str(b, "uid", { max: 128 });
        const message = typeof b.message === "string" ? b.message.slice(0, 500) : "";
        return sendResult(
          res,
          await supervisor.command(connectionId, { cmd: "friendRequest", args: { uid, message } }, 30_000),
        );
      }
      case "undo-friend-request": {
        const b = obj(parseJson(raw));
        const uid = str(b, "uid", { max: 128 });
        return sendResult(res, await supervisor.command(connectionId, { cmd: "undoFriendRequest", args: { uid } }, 30_000));
      }
      default:
        throw new HttpError(404, "not_found");
    }
  }

  return (req, res) => {
    const started = Date.now();
    const method = (req.method ?? "GET").toUpperCase();
    const pathAndQuery = req.url ?? "/";
    const path = pathAndQuery.split("?")[0] ?? "/";
    res.on("finish", () => {
      log.debug("request", { method, path, status: res.statusCode, durationMs: Date.now() - started });
    });
    void (async () => {
      try {
        const raw = await readBody(req);
        const nowSec = Math.floor((deps.now ? deps.now() : Date.now()) / 1000);
        const v = verifySignature(deps.secret, req.headers[SIGNATURE_HEADER], method, pathAndQuery, raw, nowSec);
        if (!v.ok) {
          log.warn("rejected unsigned/invalid request", { method, path, code: v.reason });
          return send(res, 401, { error: "unauthorized" });
        }
        await route(method, path, raw, res);
      } catch (err) {
        if (err instanceof HttpError) {
          if (!res.headersSent) send(res, err.status, { error: err.code });
        } else {
          log.error("request handler failed", { method, path, errName: err instanceof Error ? err.name : "Error" });
          if (!res.headersSent) send(res, 500, { error: "internal" });
        }
      }
    })();
  };
}
