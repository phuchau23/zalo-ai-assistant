// Mapping zca-js / network errors to the HTTP answers in CONTRACT §5.
//
// What zca-js 2.2.0 throws (dist/Errors, dist/utils.js):
// - ZaloApiError (err.name === "ZcaApiError") with numeric `code`: Zalo answered with error_code ≠ 0
//   (blocked stranger, wrong uid, restricted account...) → 422 rejected (permanent).
// - ZaloApiError without code, message "Request failed with status code N": HTTP not OK → 503.
// - ZaloApiError without code, "Failed to parse response data" / encrypt failures → 503 (temporary).
// - ZaloApiError without code, "Missing ..." (bad input) → 422 rejected, code null.
// - fetch/node-fetch/socket/proxy errors (TypeError "fetch failed", FetchError, AbortError,
//   ECONNREFUSED, ETIMEDOUT...) → 503 unavailable.
import type { CommandResult } from "./types.ts";

const ZCA_ERROR_NAMES = new Set(["ZcaApiError", "ZaloApiError", "ZaloApiLoginQRAborted", "ZaloApiLoginQRDeclined"]);

export type ErrorClass =
  | { kind: "rejected"; code: number | null; reason: string }
  | { kind: "unavailable" };

function rawCode(err: unknown): unknown {
  return err && typeof err === "object" ? (err as { code?: unknown }).code : undefined;
}

export function isZcaApiError(err: unknown): boolean {
  if (!err || typeof err !== "object") return false;
  const name = (err as { name?: unknown }).name;
  return typeof name === "string" && ZCA_ERROR_NAMES.has(name);
}

/** Zalo error code if this is a Zalo-side rejection. */
export function zaloErrorCode(err: unknown): number | null {
  if (!isZcaApiError(err)) return null;
  const c = rawCode(err);
  if (typeof c === "number" && Number.isFinite(c)) return c;
  if (typeof c === "string" && /^-?\d+$/.test(c)) return Number(c);
  return null;
}

/**
 * Short reason for the API, ≤ 200 chars, with the outgoing text and long digit runs removed so it
 * never carries message content or phone numbers.
 */
export function safeReason(err: unknown, sentText?: string): string {
  let msg = "";
  if (err && typeof err === "object") {
    const m = (err as { message?: unknown }).message;
    if (typeof m === "string") msg = m;
  }
  if (sentText && sentText.length > 0 && msg.includes(sentText)) msg = msg.split(sentText).join("…");
  msg = msg.replace(/\d{6,}/g, "#").replace(/[\r\n\t]+/g, " ").trim();
  if (msg.length === 0) msg = "rejected by Zalo";
  return msg.length > 200 ? msg.slice(0, 200) : msg;
}

export function classifyError(err: unknown, sentText?: string): ErrorClass {
  const code = zaloErrorCode(err);
  if (code !== null) return { kind: "rejected", code, reason: safeReason(err, sentText) };
  if (isZcaApiError(err)) {
    const m = (err as { message?: unknown }).message;
    if (typeof m === "string" && /^Missing\b/.test(m)) {
      return { kind: "rejected", code: null, reason: safeReason(err, sentText) };
    }
    return { kind: "unavailable" };
  }
  // Fake session uses this shape for a deterministic rejection.
  if (err && typeof err === "object" && (err as { name?: unknown }).name === "FakeRejected") {
    const c = rawCode(err);
    return { kind: "rejected", code: typeof c === "number" ? c : null, reason: safeReason(err, sentText) };
  }
  return { kind: "unavailable" };
}

export function errorToResult(err: unknown, sentText?: string): CommandResult {
  const c = classifyError(err, sentText);
  if (c.kind === "rejected") {
    return { status: 422, body: { error: "rejected", code: c.code, reason: c.reason } };
  }
  return { status: 503, body: { error: "unavailable" } };
}

/** True for transport-level failures (DNS, refused, reset, timeout, proxy CONNECT failure). */
export function isNetworkError(err: unknown): boolean {
  if (!err || typeof err !== "object") return false;
  if (isZcaApiError(err)) {
    const m = (err as { message?: unknown }).message;
    return typeof m === "string" && m.startsWith("Request failed with status code");
  }
  const e = err as { name?: unknown; type?: unknown };
  if (e.name === "AbortError" || e.name === "TimeoutError" || e.name === "FetchError") return true;
  if (e.name === "TypeError") return true; // undici "fetch failed"
  const c = rawCode(err);
  if (typeof c === "string" && /^(E[A-Z_]+|UND_ERR_[A-Z_]+|ERR_[A-Z_]+)$/.test(c)) return true;
  const cause = (err as { cause?: unknown }).cause;
  return cause !== undefined && cause !== err && isNetworkError(cause);
}
