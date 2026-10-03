# Ghi chú API Zalo (đã kiểm chứng)

Chỉ ghi thông tin **đã kiểm tra trên docs chính thức** (developers.zalo.me) hoặc bằng thử nghiệm thật trên OA test, kèm **ngày kiểm tra** và **link nguồn**. Không ghi theo trí nhớ, blog, hay thư viện không chính thức (CLAUDE.md mục 8, skill `zalo-api-work`).

Quy ước trạng thái mỗi mục:
- `CHƯA KIỂM CHỨNG` — chưa ai xem docs; **không được code** dựa trên mục này.
- `ĐÃ KIỂM CHỨNG (YYYY-MM-DD)` — kèm link nguồn và người kiểm tra.
- Mục kiểm chứng cũ hơn 3 tháng → kiểm tra lại trước khi đụng code liên quan.

Hoàn thành file này là việc **đầu tiên của M4**, trước khi viết code Zalo.

---

## 1. Ứng dụng và quyền

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi: cách tạo Zalo App, liên kết OA, các quyền (scope) cần xin cho: nhận tin, gửi tin tư vấn, đọc thông tin người theo dõi; quy trình và thời gian xét duyệt app.
- Nguồn: _

## 2. OAuth cấp quyền OA cho app (kết nối OA của doanh nghiệp)

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi:
  - URL trang cấp quyền và tham số (app id, redirect uri, `state`, có bắt buộc PKCE `code_challenge` không, phương thức S256?).
  - Endpoint đổi `code` lấy token: URL, method, header (secret gửi ở đâu), body, định dạng response, mã lỗi.
  - Redirect URI: có phải đăng ký trước không, có cho `localhost`/URL ngrok không.
  - Một app kết nối nhiều OA của nhiều doanh nghiệp: có giới hạn gì không; OA id lấy từ đâu sau khi cấp quyền.
- Nguồn: _

## 3. Access token và refresh token

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi:
  - Thời hạn access token, thời hạn refresh token.
  - Refresh token **dùng được một lần hay nhiều lần** (quyết định thiết kế Redis lock — CLAUDE.md mục 3).
  - Endpoint làm mới token: URL, tham số, response.
  - Mã lỗi khi token hết hạn / bị thu hồi / DN gỡ quyền app (→ đánh dấu `needs_reauth`).
- Nguồn: _

## 4. Webhook (Zalo gọi vào hệ thống)

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi:
  - Cách đăng ký URL webhook (theo app hay theo OA), yêu cầu HTTPS, cách Zalo xác minh URL.
  - **Cơ chế chữ ký:** header nào, thuật toán (ví dụ HMAC/SHA256 trên chuỗi gì: app id + body + timestamp + secret?), cách kiểm, có chống replay (timestamp) không.
  - Định dạng payload chung: OA id, user id người gửi, message id (dùng để dedupe), timestamp.
  - Zalo có gọi lại (retry) khi không nhận được 200 không; thời gian chờ tối đa của Zalo trước khi coi là lỗi.
- Nguồn: _

## 5. Các loại sự kiện cần xử lý (GĐ1)

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi tên sự kiện + payload mẫu (đã xóa PII, lưu ở `tests/ZaloAi.UnitTests/Fixtures/Zalo/`):
  - người dùng gửi tin văn bản;
  - người dùng gửi ảnh / file / sticker / vị trí;
  - người dùng theo dõi / bỏ theo dõi OA;
  - OA gửi tin (khi nhân viên trả lời trực tiếp trong app Zalo OA) — có sự kiện này không? (câu hỏi mở trong PROGRESS.md)
- Nguồn: _

## 6. Gửi tin tư vấn (trả lời khách)

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi:
  - Endpoint gửi tin văn bản (và ảnh nếu cần): URL, header token, body, response (message id), mã lỗi thường gặp.
  - **Khung thời gian** OA được nhắn tư vấn sau tin cuối của khách (bao nhiêu giờ/ngày), số tin tối đa trong khung — ảnh hưởng `FEATURE-SPECS.md` mục 1, 2 (câu hỏi mở trong PROGRESS.md).
  - Giới hạn độ dài tin, ký tự đặc biệt, link.
- Nguồn: _

## 7. Giới hạn tốc độ và hạn mức

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi: giới hạn số request/giây hoặc /ngày theo app và theo OA, mã lỗi khi vượt, khuyến nghị backoff.
- Nguồn: _

## 8. ZNS (Zalo Notification Service) — Giai đoạn 2

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi: điều kiện dùng, quy trình duyệt mẫu tin, giá mỗi tin, endpoint gửi, quy định nội dung (không quảng cáo...).
- Nguồn: _

## 9. Chính sách và điều khoản

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi: quy định của Zalo về chatbot/trợ lý AI trên OA, nội dung cấm, yêu cầu báo cho người dùng là bot, quy định lưu trữ dữ liệu người dùng Zalo, điều kiện thương mại khi app phục vụ nhiều doanh nghiệp.
- Nguồn: _

---

## Nhật ký kiểm tra

| Ngày | Mục | Người kiểm tra | Ghi chú |
| --- | --- | --- | --- |
| | | | |
