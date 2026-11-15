// End-to-end smoke: the real process model (`node src/main.ts`, one forked child per login/nick,
// Node type stripping) in fake mode, against a mock API on node:http.
import { spawn, type ChildProcess } from "node:child_process";
import { createServer, type Server } from "node:http";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { signRequest, verifySignature } from "../src/hmac.ts";

const SECRET = "e2e-shared-secret-0123456789-abcdefgh";
const MAIN = fileURLToPath(new URL("../src/main.ts", import.meta.url));

type Call = { path: string; body: Record<string, any> };
const calls: Call[] = [];
let badSignatures = 0;
let api: Server;
let child: ChildProcess;
let sidecarPort = 0;
let output = "";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
async function waitFor<T>(fn: () => T | undefined | null | false, timeoutMs = 10_000): Promise<T> {
  const end = Date.now() + timeoutMs;
  for (;;) {
    const v = fn();
    if (v) return v;
    if (Date.now() > end) throw new Error("timeout; sidecar output:\n" + output);
    await sleep(25);
  }
}

async function call(method: string, path: string, body?: unknown) {
  const raw = body === undefined ? "" : JSON.stringify(body);
  const res = await fetch(`http://127.0.0.1:${sidecarPort}${path}`, {
    method,
    headers: { "content-type": "application/json", "x-remi-signature": signRequest(SECRET, method, path, raw) },
    body: raw || undefined,
  });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}

beforeAll(async () => {
  api = createServer((req, res) => {
    let raw = "";
    req.on("data", (c) => (raw += c));
    req.on("end", () => {
      if (!verifySignature(SECRET, req.headers["x-remi-signature"], req.method ?? "", req.url ?? "", raw).ok) badSignatures++;
      const body = raw ? JSON.parse(raw) : {};
      calls.push({ path: req.url ?? "", body });
      const json = (status: number, b: unknown) => {
        res.writeHead(status, { "content-type": "application/json" });
        res.end(JSON.stringify(b));
      };
      if (req.url === "/internal/zalo-personal/sync") return json(200, { leaseGranted: true, accounts: [] });
      if (req.url === "/internal/zalo-personal/login-result") return json(200, { connectionId: "conn-e2e" });
      res.writeHead(204);
      res.end();
    });
  });
  await new Promise<void>((r) => api.listen(0, "127.0.0.1", () => r()));
  const apiPort = (api.address() as { port: number }).port;

  child = spawn(process.execPath, [MAIN], {
    env: {
      ...process.env,
      REMI_SHARED_SECRET: SECRET,
      REMI_MODE: "fake",
      REMI_PORT: "0",
      REMI_API_URL: `http://127.0.0.1:${apiPort}`,
      REMI_INSTANCE_ID: "e2e",
      REMI_LOG_LEVEL: "info",
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stdout!.on("data", (d) => (output += d));
  child.stderr!.on("data", (d) => (output += d));
  const line = await waitFor(() => output.split("\n").find((l) => l.includes('"sidecar listening"')), 15_000);
  sidecarPort = JSON.parse(line).port;
}, 30_000);

afterAll(async () => {
  child?.kill();
  await new Promise((r) => api?.close(() => r(null)));
});

describe("e2e (fake mode, real child processes)", () => {
  it("connects a nick by QR, forwards an incoming message and sends a reply", async () => {
    expect((await call("GET", "/health")).body).toMatchObject({ instance: "e2e", mode: "fake" });
    await waitFor(() => calls.find((c) => c.path.endsWith("/sync")));

    expect((await call("POST", "/login-qr", { loginId: "e2e-login", userAgent: "UA-e2e", proxyUrl: null })).status).toBe(202);
    await waitFor(() => calls.find((c) => c.body.state === "qr"));
    expect((await call("POST", "/dev/simulate", { action: "scan_qr", loginId: "e2e-login" })).status).toBe(204);
    const result = await waitFor(() => calls.find((c) => c.path.endsWith("/login-result")));
    expect(result.body.uid).toMatch(/^fake-\d{6}$/);
    await waitFor(() =>
      calls.find((c) => c.path.endsWith("/events") && c.body.events.some((e: any) => e.state === "connected")),
    );

    await call("POST", "/dev/simulate", { action: "incoming", connectionId: "conn-e2e", fromUid: "cust-9", text: "alo" });
    const msg = await waitFor(() =>
      calls.find((c) => c.path.endsWith("/events") && c.body.events.some((e: any) => e.type === "message")),
    );
    expect(msg.body.events[0]).toMatchObject({ type: "message", threadId: "cust-9", text: "alo", isSelf: false });

    const sent = await call("POST", "/accounts/conn-e2e/send", { messageId: "e2e-m1", threadId: "cust-9", text: "dạ", typingMs: 10 });
    expect(sent.status).toBe(200);
    expect(typeof sent.body.msgId).toBe("string");

    const health = await call("GET", "/health");
    expect(health.body.accounts).toEqual([{ connectionId: "conn-e2e", state: "connected" }]);
    expect(badSignatures).toBe(0);
    expect(output).not.toContain("UA-e2e");
    expect(output).not.toContain(SECRET);
  }, 30_000);
});
