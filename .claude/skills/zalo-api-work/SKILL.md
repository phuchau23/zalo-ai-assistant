---
name: zalo-api-work
description: Làm bất cứ việc gì với API Zalo (OAuth, webhook, gửi tin, ZNS, token). Dùng mỗi khi code đụng tới src/ZaloAi.Channels/Zalo hoặc callback/webhook Zalo.
---

# Làm việc với API Zalo

**Không đoán** endpoint, tham số, cơ chế chữ ký, thời hạn token (CLAUDE.md mục 8).

1. Đọc `docs/zalo-api-notes.md` trước. Mục nào còn "CHƯA KIỂM CHỨNG", hoặc ngày kiểm tra cũ hơn 3 tháng → nhờ chủ dự án kiểm tra docs chính thức (developers.zalo.me), ghi lại kèm ngày và link nguồn rồi mới code.
2. Mọi lệnh gọi Zalo đi qua `ZaloClient` (typed HttpClient) trong adapter, có timeout, retry (resilience handler), log (đã redact).
3. Token OA lưu mã hóa bằng `IFieldEncryptor` (`access_token_enc`, `refresh_token_enc`). Không log token, kể cả khi lỗi.
4. Refresh token: dùng Redis lock theo OA, đọc lại token sau khi lấy được lock (có thể worker khác đã refresh), lưu token mới trong cùng transaction.
5. Lỗi token không hợp lệ/bị thu hồi → đánh dấu connection `needs_reauth`, thông báo DN, dừng gửi.
6. Webhook: xác thực chữ ký → dedupe `external_msg_id` → tìm connection theo OA ID → enqueue job → trả 200. Không gọi AI trong request webhook. Sai chữ ký → 401.
7. Callback OAuth có `code` trong query string: không log URL đầy đủ (Sentry đã bỏ query string qua `SentryScrubber`).
8. Tôn trọng khung thời gian được phép nhắn chủ động; ngoài khung chỉ gửi ZNS theo mẫu đã duyệt.
9. Test bằng fixture payload thật (đã xóa PII) lưu ở `tests/ZaloAi.UnitTests/Fixtures/Zalo/`.
