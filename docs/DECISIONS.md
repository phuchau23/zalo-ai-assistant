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

## 2026-10-02 — Database nền (M1 bước 3)

- **Bảng tạo dần theo module:** migration đầu chỉ có `tenants`, `users`, `memberships`, `audit_logs`. Các bảng khác (documents/chunks M2, contacts/conversations/messages/usage_records M3, channel_connections M4) thêm đúng lúc module dùng tới (chủ dự án chốt).
- **ID:** UUID v7 sinh ở code (`Guid.CreateVersion7()`): không đoán được, sắp xếp được theo thời gian.
- **Enum lưu chữ thường** (`active`, `owner`) qua `LowercaseEnumConverter`, để đọc DB trực tiếp vẫn hiểu.
- **Cô lập tenant trong `AppDbContext`:** global query filter cho mọi `ITenantOwned` và bảng `tenants` (`id = tenant hiện tại`); chưa có tenant → không thấy dòng nào. `SaveChanges` ném `TenantIsolationException` khi thêm/sửa/xóa dòng không thuộc tenant hiện tại, kể cả đổi `tenant_id`. Repository kiểm tra thêm `tenantId` tham số khớp `ITenantContext` (lớp 3).
- **`users` là bảng toàn cục** (một người có thể ở nhiều tenant qua `memberships`). **`audit_logs` không có global filter** vì `tenant_id` có thể null (B4); `AuditLogRepository` tự lọc. Xóa tenant xóa luôn audit log của tenant đó.
- **Seed** chạy từng tenant trong scope riêng có set tenant, đi qua đúng luật cô lập như code thật. Chỉ chạy ở Development.
- **Version NuGet:** bật `CentralPackageTransitivePinningEnabled` và ghim EF Core 10.0.12, vì Npgsql kéo EF bản cũ hơn gây xung đột.
- Code migration sinh tự động được loại khỏi analyzer (`Persistence/Migrations/.editorconfig`), không sửa tay.

## 2026-10-02 — Đăng nhập và phân quyền (M1 bước 4)

- **Cookie ASP.NET Core** tên `zaloai.session`: httpOnly, `SameSite=Lax`, `Secure` bắt buộc ngoài Development/Testing, hết hạn 12h trượt. Cookie chỉ chứa `sub` (userId) và `tid` (tenant đang chọn).
- **Kiểm lại quyền mỗi request** (`TenantContextMiddleware`): đọc user, membership, trạng thái tenant từ DB. Xóa user → 401; mất membership hoặc tenant không `active` → 403 `no_active_tenant`. Đổi lại 2 truy vấn nhỏ mỗi request; cache sau nếu cần.
- **CSRF:** không dùng antiforgery token. Dựa vào `SameSite=Lax` + API chỉ nhận JSON (form của trang khác không gửi được `application/json` mà không qua CORS preflight). Lax thay vì Strict để callback OAuth Zalo (M4) vẫn mang cookie.
- **Đăng nhập sai:** cùng một thông báo và cùng thời gian xử lý (hash giả) cho email không tồn tại và sai mật khẩu. Mọi lần đăng nhập/đăng xuất/đổi tenant ghi `audit_logs`.
- **`AccessQueries`** dùng `IgnoreQueryFilters()` có chủ đích: là nơi quyết định tenant nên chạy trước khi có tenant context; chỉ lọc theo userId đã xác thực, chỉ trả tên + vai trò.
- **Khóa Data Protection** (mã hóa cookie) lưu bảng `data_protection_keys` qua `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (chủ dự án duyệt): deploy lại hoặc chạy nhiều bản API không đăng xuất người dùng.
- **Lỗi API** trả ProblemDetails kèm `code` ổn định (`invalid_credentials`, `unauthenticated`, `forbidden`, `no_active_tenant`, `not_found`, `validation_failed`, `rate_limited`, `internal_error`). Lỗi validate trả tên trường camelCase.
- **OpenAPI** chỉ mở ngoài Production (`/openapi/v1.json`).

## 2026-10-02 — Hàng đợi job (M1 bước 5)

- **Hangfire lưu trong Postgres**, schema riêng `hangfire`, bảng do Hangfire tự quản (không qua migration EF). **Chỉ Worker tạo/nâng cấp bảng** (`PrepareSchemaIfNecessary`); Api chỉ dùng, để Api khởi động không phụ thuộc thao tác DDL. Lệnh `seed` và bộ test gọi `HangfireSetup.EnsureSchemaAsync` để bảng có sẵn trên máy dev.
- **Job có dữ liệu tenant:** tham số đầu là `tenantId` (do API/webhook xác định), dòng đầu `TenantContext.Set(...)`. Hangfire tạo DI scope riêng cho mỗi lần chạy. Khuôn mẫu: `SampleTenantJob`.
- **Retry toàn cục:** 5 lần, chờ 10s, 30s, 90s, 270s, 810s (thay mặc định 10 lần của Hangfire). Hết lượt → trạng thái Failed (dead-letter, chạy lại tay trên dashboard) + log Error qua `JobFailureAlertFilter` (chỉ ghi tên job + loại lỗi, không ghi tham số/message vì có thể chứa dữ liệu khách).
- **Dashboard `/hangfire` chỉ super admin** (hiện job của mọi tenant, chạy lại/xóa được). Không hiện connection string.
- Worker hỏi hàng đợi mỗi 5s (`Jobs:QueuePollSeconds`), 10 job song song (`Jobs:WorkerCount`).
- Hangfire.Core kéo `Newtonsoft.Json` 11.0.1 có lỗ hổng (GHSA-5crp-9r3c-p9vr) → ghim 13.0.4 trong `Directory.Packages.props`.
