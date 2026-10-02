# Quyết định kỹ thuật

Mỗi quyết định: ngày, nội dung, lý do, hệ quả.

## 2026-10-01 — Tách 2 repo: BE .NET + FE Next.js

**Quyết định:** Bỏ monorepo pnpm (Node.js/Fastify/BullMQ/Drizzle). Dự án gồm 2 repo nằm cạnh nhau trong `C:\Zalo_Tool\`:
- `zalo-ai-assistant`: backend **.NET 10** (ASP.NET Core API + Hangfire Worker + EF Core). Giữ CLAUDE.md và `docs/` làm nguồn chính.
- `zalo-ai-portal`: frontend **Next.js** (App Router, TypeScript, Tailwind, shadcn/ui).

**Lý do:** chủ dự án quen C#/.NET hơn Node, nên tự đọc, sửa, vận hành BE được.

**Hệ quả:**
- Không dùng chung type giữa FE/BE. BE xuất OpenAPI, FE sinh client bằng `openapi-typescript` (`pnpm gen:api`). Đổi API thì phải sinh lại và commit ở cả 2 repo.
- Hàng đợi dùng **Hangfire + PostgreSQL storage** (không dùng Redis cho queue): đơn giản, có dashboard và retry sẵn. Redis vẫn dùng cho lock refresh token, OAuth state, dedupe, rate limit gửi.
- Cô lập tenant có 2 lớp: repository nhận `tenantId` + EF Core global query filter.
- Mật khẩu hash bằng `PasswordHasher` của ASP.NET Core Identity (PBKDF2) thay vì argon2, để không thêm thư viện ngoài. Đổi sang argon2 sau được (hash cũ vẫn verify được nếu lưu kèm phiên bản thuật toán).
- FE gọi BE qua rewrite `/api/*` của Next.js để cookie session cùng domain.
- Kế hoạch M1 cũ (8 bước, pnpm) bị thay thế. Câu hỏi B1 cũ (thư viện Node) không còn áp dụng.
- SDK AI cho .NET: dùng SDK chính thức nếu ổn định, không thì gọi REST. Chốt ở M3.

## 2026-10-01 — Mở ngành bằng mẫu ngành, không train model

**Quyết định:** Hệ thống mở được cho mọi ngành bằng **mẫu ngành** (persona, quy tắc, câu cấm, dấu hiệu nguy hiểm, trường lead, flow mẫu, eval) + **kho kiến thức riêng của DN** (RAG). Không fine-tune/train model theo ngành. Ngành chia mức rủi ro thấp / trung bình / cao / chưa mở. Chi tiết: `docs/INDUSTRIES.md`.

**Lý do:** mở ngành mới nhanh (2–4 ngày), không tốn chi phí huấn luyện, đổi model AI không mất gì, bot chỉ nói theo dữ liệu được nạp.

**Hệ quả:** ngành rủi ro cao (spa/thẩm mỹ, nha khoa) bắt buộc bộ an toàn y tế: chỉ giải thích bệnh từ tài liệu DN đánh dấu đã được chuyên môn duyệt, không chẩn đoán/kê thuốc/hứa kết quả, dấu hiệu nguy hiểm → chuyển khẩn cấp, eval an toàn đạt 100% mới bật.

## 2026-10-01 — Câu chuyển tiếp và trang "Cần chăm sóc" (đề xuất, chủ dự án có thể đổi)

- Phase 5: hệ thống tự gửi câu chuyển tiếp khi bot ↔ nhân viên đổi người trả lời (Zalo không tự báo). Câu "bot chuyển người" luôn bật; chữ ký nhân viên mặc định bật.
- Phase 6: thêm trang "Cần chăm sóc" — AI gợi ý khách nhân viên nên chủ động nhắn. Mặc định: có tin nháp AI (tenant tắt được), tự gán cho nhân viên chat gần nhất.
- Nhắc lịch tự động: để Giai đoạn 2 (đưa lên v1 nếu ngành đầu là spa/nha khoa, +1–2 tuần).
- Chi tiết: `docs/FEATURE-SPECS.md`.

## 2026-10-02 — Mã hóa trường nhạy cảm và che log (M1 bước 2)

- **Mã hóa:** AES-256-GCM, mỗi lần mã hóa nonce ngẫu nhiên 12 bytes, tag 16 bytes. Lưu chuỗi `v1:{nonce}:{tag}:{ciphertext}` (base64). `v1` dành chỗ cho xoay khóa: sau này thêm `v2` với khóa mới, giải mã theo tiền tố, job mã hóa lại dữ liệu cũ.
- **Khóa:** `Security__EncryptionKey` (32 bytes base64), chỉ đặt qua env/user-secrets. Thiếu hoặc sai → app không khởi động. Mất khóa production = mất dữ liệu đã mã hóa → chủ dự án lưu khóa prod trong trình quản lý mật khẩu.
- **Che log:** Serilog enricher che (1) property có tên nhạy cảm (token, password, secret, apikey, phone, content...) kể cả trong object lồng nhau, (2) SĐT VN và email trong mọi chuỗi. Đây là lớp phòng thủ thứ 2; code vẫn không được chủ động log token/PII. Chưa che nội dung trong message của exception (ghi nhận nợ kỹ thuật).
- Api và Worker dùng chung một UserSecretsId (`zaloai-dev`) để dev chỉ cấu hình secret một lần.
- Dev log dạng chữ dễ đọc; môi trường khác log JSON (`RenderedCompactJsonFormatter`).
