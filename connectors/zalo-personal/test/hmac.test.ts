import { createHmac } from "node:crypto";
import { describe, expect, it } from "vitest";
import { computeSignature, signRequest, verifySignature } from "../src/hmac.ts";

const SECRET = "s".repeat(32) + "-shared-secret";
const NOW = 1_731_400_000;
const PATH = "/accounts/0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b/send";
const BODY = JSON.stringify({ messageId: "m1", threadId: "123", text: "Xin chào", typingMs: 0 });

describe("hmac", () => {
  it("matches the contract formula t.METHOD.path.body with lowercase hex", () => {
    const expected = createHmac("sha256", Buffer.from(SECRET, "utf8"))
      .update(`${NOW}.POST.${PATH}.${BODY}`, "utf8")
      .digest("hex");
    expect(computeSignature(SECRET, NOW, "post", PATH, BODY)).toBe(expected);
    expect(signRequest(SECRET, "POST", PATH, BODY, NOW)).toBe(`t=${NOW},v1=${expected}`);
  });

  it("accepts a good signature", () => {
    const h = signRequest(SECRET, "POST", PATH, BODY, NOW);
    expect(verifySignature(SECRET, h, "POST", PATH, BODY, NOW + 10)).toEqual({ ok: true });
  });

  it("accepts GET with empty body", () => {
    const h = signRequest(SECRET, "GET", "/health", "", NOW);
    expect(verifySignature(SECRET, h, "GET", "/health", "", NOW)).toEqual({ ok: true });
  });

  it("rejects missing and malformed headers", () => {
    expect(verifySignature(SECRET, undefined, "GET", "/health", "", NOW)).toEqual({ ok: false, reason: "missing" });
    expect(verifySignature(SECRET, "garbage", "GET", "/health", "", NOW)).toMatchObject({ reason: "malformed" });
    expect(verifySignature(SECRET, `t=${NOW},v1=ABC`, "GET", "/health", "", NOW)).toMatchObject({ reason: "malformed" });
    expect(verifySignature(SECRET, ["a", "b"], "GET", "/health", "", NOW)).toMatchObject({ reason: "malformed" });
  });

  it("rejects a wrong secret", () => {
    const h = signRequest("w".repeat(40), "POST", PATH, BODY, NOW);
    expect(verifySignature(SECRET, h, "POST", PATH, BODY, NOW)).toMatchObject({ reason: "mismatch" });
  });

  it("rejects stale or future timestamps beyond 300 s", () => {
    const h = signRequest(SECRET, "POST", PATH, BODY, NOW);
    expect(verifySignature(SECRET, h, "POST", PATH, BODY, NOW + 300)).toEqual({ ok: true });
    expect(verifySignature(SECRET, h, "POST", PATH, BODY, NOW + 301)).toMatchObject({ reason: "stale" });
    expect(verifySignature(SECRET, h, "POST", PATH, BODY, NOW - 301)).toMatchObject({ reason: "stale" });
  });

  it("rejects tampered body, path, method or timestamp", () => {
    const h = signRequest(SECRET, "POST", PATH, BODY, NOW);
    expect(verifySignature(SECRET, h, "POST", PATH, BODY.replace("chào", "chao"), NOW)).toMatchObject({
      reason: "mismatch",
    });
    expect(verifySignature(SECRET, h, "POST", PATH.replace("send", "stop"), BODY, NOW)).toMatchObject({
      reason: "mismatch",
    });
    expect(verifySignature(SECRET, h, "PUT", PATH, BODY, NOW)).toMatchObject({ reason: "mismatch" });
    const forged = h.replace(`t=${NOW}`, `t=${NOW + 1}`);
    expect(verifySignature(SECRET, forged, "POST", PATH, BODY, NOW)).toMatchObject({ reason: "mismatch" });
    expect(verifySignature(SECRET, h, "POST", PATH + "?x=1", BODY, NOW)).toMatchObject({ reason: "mismatch" });
  });
});
