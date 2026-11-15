// HMAC request signing (CONTRACT §1).
//   X-Remi-Signature: t=<unix seconds>,v1=<lowercase hex>
//   v1 = HMAC-SHA256(key = UTF-8(secret), message = UTF-8(`${t}.${METHOD}.${pathAndQuery}.${body}`))
import { createHmac, timingSafeEqual } from "node:crypto";

export const SIGNATURE_HEADER = "x-remi-signature";
export const MAX_SKEW_SECONDS = 300;

export function computeSignature(
  secret: string,
  t: number,
  method: string,
  pathAndQuery: string,
  body: string,
): string {
  return createHmac("sha256", Buffer.from(secret, "utf8"))
    .update(Buffer.from(`${t}.${method.toUpperCase()}.${pathAndQuery}.${body}`, "utf8"))
    .digest("hex");
}

export function signRequest(
  secret: string,
  method: string,
  pathAndQuery: string,
  body: string,
  nowSeconds: number = Math.floor(Date.now() / 1000),
): string {
  return `t=${nowSeconds},v1=${computeSignature(secret, nowSeconds, method, pathAndQuery, body)}`;
}

export type VerifyResult = { ok: true } | { ok: false; reason: "missing" | "malformed" | "stale" | "mismatch" };

const HEADER_RE = /^t=(\d{1,12}),v1=([0-9a-f]{64})$/;

export function verifySignature(
  secret: string,
  header: string | string[] | undefined,
  method: string,
  pathAndQuery: string,
  body: string,
  nowSeconds: number = Math.floor(Date.now() / 1000),
): VerifyResult {
  if (header === undefined) return { ok: false, reason: "missing" };
  if (Array.isArray(header)) return { ok: false, reason: "malformed" };
  const match = HEADER_RE.exec(header.trim());
  if (!match) return { ok: false, reason: "malformed" };
  const t = Number(match[1]);
  if (!Number.isSafeInteger(t)) return { ok: false, reason: "malformed" };
  if (Math.abs(nowSeconds - t) > MAX_SKEW_SECONDS) return { ok: false, reason: "stale" };
  const expected = Buffer.from(computeSignature(secret, t, method, pathAndQuery, body), "hex");
  const given = Buffer.from(match[2]!, "hex");
  if (given.length !== expected.length) return { ok: false, reason: "mismatch" };
  return timingSafeEqual(given, expected) ? { ok: true } : { ok: false, reason: "mismatch" };
}
