# Tiến độ

## Đang làm
- Module: M1 — Nền tảng
- Task: bước 6 xong (FE nhánh `feat/m1-portal-login-settings`), chờ chủ dự án chạy thử + commit; tiếp theo bước 7 (Sentry, backup DB, CI)
- Chặn bởi: không

## Kế hoạch M1 (bản .NET, 2026-10-01) — đã duyệt 2026-10-02
Thay kế hoạch pnpm ngày 2026-09-30 (xem DECISIONS.md). 8 bước, mỗi bước 1 commit, dừng sau mỗi bước để chủ dự án chạy thử:
1. `chore(m1)`: BE solution .NET 10 (`ZaloAi.sln`, 7 project `src/` + 2 project `tests/`), `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, docker-compose (pgvector pg16, redis 7), `.env.example`
2. `feat(m1)`: Core + Infrastructure nền: Options có validate, Serilog có redact, crypto AES-256-GCM (định dạng `v1:iv:tag:ct`), exception chung, `ITenantContext`
3. `feat(m1)`: EF Core: DbContext, migration đầu, global query filter tenant, repository có tenantId, seed 2 tenant, test cô lập tenant (Testcontainers)
4. `feat(m1)`: Api: `/health`, đăng nhập/đăng xuất (cookie httpOnly), gắn tenant + role, rate limit, OpenAPI, endpoint cài đặt tenant
5. `feat(m1)`: Worker: Hangfire server (Postgres storage), job mẫu, retry backoff, job lỗi hết lượt → log + cảnh báo
6. `feat(m1)` (repo FE): login, layout, trang cài đặt tenant; rewrite `/api/*` → BE; `pnpm gen:api`
7. `chore(m1)`: Sentry (api + worker), script backup DB, CI GitHub Actions cho cả 2 repo
8. `docs(m1)`: tách skills ra `.claude/skills/`, tạo `docs/zalo-api-notes.md`

## Hướng dẫn chủ dự án — Phần A: cài công cụ
- [x] A1: pnpm (12.8.1) + Node (24) — xong 2026-10-01
- [x] A2: .NET 10 SDK 10.0.401 + dotnet-ef 10.0.12 — xong 2026-10-02
- [x] A3: Docker Desktop (WSL 2) — xong 2026-10-02 (phải cài WSL trước, nếu không Docker báo nhầm "Virtualization support not detected")
- [x] A4: git 2.55 — có sẵn
- [x] A6: tắt Smart App Control (2026-10-02) — nó chặn DLL tự build (dotnet ef, test) và pnpm. Không bật lại được nếu không reset Windows; Defender vẫn chạy
- [ ] A5: tạo repo GitHub cho `zalo-ai-portal` (repo FE) khi muốn push

## Phần B: quyết định của chủ dự án (chốt 2026-10-02)
- [x] B1: duyệt hết thư viện dưới đây
  - BE (NuGet): Npgsql.EntityFrameworkCore.PostgreSQL, Pgvector.EntityFrameworkCore, EFCore.NamingConventions, Microsoft.EntityFrameworkCore.Design, Hangfire.AspNetCore, Hangfire.PostgreSql, StackExchange.Redis, FluentValidation, Serilog.AspNetCore, Sentry.AspNetCore, Microsoft.Extensions.Http.Resilience, Microsoft.AspNetCore.OpenApi, Microsoft.Extensions.Identity.Core (PasswordHasher)
  - BE test: xunit, Microsoft.AspNetCore.Mvc.Testing, Testcontainers.PostgreSql, Testcontainers.Redis, Shouldly
  - FE: openapi-typescript, openapi-fetch, shadcn/ui (kèm các gói nó cài), vitest
- [x] B2: chỉ super admin tạo tenant (qua CLI), chưa mở đăng ký công khai
- [x] B3: embedding 768 chiều
- [x] B4: đồng ý cả 3: `audit_logs.tenant_id` nullable; unique `(tenant_id, external_msg_id)`; session lưu bằng cookie ticket của ASP.NET Core thay vì bảng `sessions`
- [x] B5: 2 tenant mẫu: "Khoa Học Nguyệt Đạo" (bấm huyệt, massage → slug `spa`, rủi ro Cao) và "Sửa nhà An Phát" (sửa nhà, rủi ro thấp)

## Đã xong
- [x] M1 bước 6: FE `zalo-ai-portal`: rewrite `/api/*` + `/hangfire` → BE, `proxy.ts` chặn sơ bộ khi chưa có cookie, trang đăng nhập, khung trang (đổi tenant, đăng xuất, link Hangfire cho super admin), trang cài đặt doanh nghiệp (owner sửa, staff xem), `pnpm gen:api` (openapi-typescript + openapi-fetch), shadcn/ui, vitest. BE thêm `GET /industries`, lỗi validate tiếng Việt (2026-10-02)
- [x] M1 bước 5: Hangfire + Postgres (schema `hangfire`), Api chỉ đẩy job, Worker chạy job; job theo tenant (set tenant context đầu job); retry 5 lần backoff 10s→13.5 phút, hết lượt → Failed + log Error; dashboard `/hangfire` chỉ super admin; `POST /dev/jobs/sample`; tổng 72 test (2026-10-02)
- [x] M1 bước 4: đăng nhập cookie (login, logout, me, switch-tenant), middleware kiểm membership + trạng thái tenant mỗi request, phân quyền owner/staff, `GET/PUT /tenant/settings` (FluentValidation, audit log), rate limit login 5/phút/IP, `/health` kiểm Postgres, ProblemDetails, OpenAPI `/openapi/v1.json`, khóa cookie lưu Postgres (`data_protection_keys`); tổng 68 test (2026-10-02)
- [x] M1 bước 3: EF Core + Npgsql (snake_case, pgvector), migration `InitialCreate` (tenants, users, memberships, audit_logs), global query filter + chặn ghi sai tenant trong SaveChanges, repository có tenantId, lệnh `seed` (2 tenant mẫu + super admin), 10 test cô lập/seed bằng Testcontainers; tổng 46 test (2026-10-02)
- [x] M1 bước 2: Options có validate khi khởi động (App, Security, Ai, Zalo), mã hóa AES-256-GCM `v1:nonce:tag:ct`, Serilog che token/PII (theo tên field + SĐT VN/email), exception chung, `ITenantContext`; 36 test (2026-10-02)
- [x] M1 bước 1: solution `ZaloAi.sln` (7 project src + 2 test), `Directory.Build.props` (net10, nullable, warnings as errors, analyzers), `Directory.Packages.props`, `.editorconfig`, `global.json` (SDK 10.0.401), docker-compose (pgvector pg16 + redis 7), `.env.example`; `/health` tạm + test smoke (2026-10-02)
- [x] Chốt tách 2 repo: BE .NET (`zalo-ai-assistant`) + FE Next.js (`zalo-ai-portal`), queue Hangfire + Postgres (2026-10-01)
- [x] Tạo project FE `C:\Zalo_Tool\zalo-ai-portal` (Next.js 16, TS strict, Tailwind 4, ESLint, App Router, `src/`); lint/typecheck/build pass (2026-10-01)
- [x] Viết ROADMAP.md (10 phase đến phát hành), FEATURE-SPECS.md (chuyển tiếp, "Cần chăm sóc", tình huống chăm sóc), INDUSTRIES.md (mẫu ngành, bộ an toàn y tế) (2026-10-01)

## Vấn đề mở / nợ kỹ thuật
- Test "chuyển dòng sang tenant khác" hiện chỉ chạy trên memberships (tenant_id thuộc khóa chính, EF tự chặn). Thêm test cho bảng đầu tiên có tenant_id ngoài khóa (documents, M2)
- Deploy: cấu hình ForwardedHeaders sau reverse proxy, nếu không rate limit login tính theo IP của proxy (gộp mọi khách)
- Đăng xuất chưa thu hồi cookie phía server: cookie bị đánh cắp còn dùng được tới khi hết hạn (12h trượt). Xóa user/membership thì mất quyền ngay. Cân nhắc thêm "security stamp" trên users (đổi mật khẩu, đăng xuất mọi nơi)
- Super admin xem/chuyển sang tenant bất kỳ: làm ở M6
- FE chưa có test giao diện (chỉ test hàm thuần bằng vitest). Cân nhắc Playwright cho luồng đăng nhập ở tuần 7–8 (thư viện mới, cần duyệt)
- `pnpm gen:api` cần BE đang chạy. Có thể cho BE xuất file openapi lúc build (thêm gói Microsoft.Extensions.ApiDescription.Server) nếu bất tiện
- Cảnh báo job lỗi hiện chỉ là log Error: nối Sentry ở bước 7, Telegram ở M5
- Log: message của exception (ví dụ lỗi từ Zalo/AI trả về) chưa được che PII — xử lý khi viết ZaloClient (M4) và AI provider (M3)
- Cân nhắc Postgres Row Level Security làm lớp phòng thủ thứ 3 (sau M1)
- Next.js 16 có thay đổi lớn so với bản cũ: đọc `node_modules/next/dist/docs/` trước khi code FE
- Khoa Học Nguyệt Đạo: nếu DN quảng cáo chữa bệnh (châm cứu, trị liệu YHCT) → thường cần giấy phép, thành ngành "chưa mở", hỏi luật sư. Chỉ massage/bấm huyệt thư giãn thì xếp `spa`
- Chọn SDK AI .NET (chính thức hay REST) ở M3
- Xác minh ở Phase 4 (docs Zalo): thời gian OA được nhắn tư vấn sau tin cuối của khách; webhook có sự kiện "OA gửi tin" khi nhân viên trả lời trong app Zalo không

## Việc chủ dự án cần làm
- Hoàn thành Phần A và trả lời Phần B ở trên
- Chọn ngành đầu tiên từ `docs/INDUSTRIES.md`; xem lại các mục "(đề xuất)" trong `docs/FEATURE-SPECS.md`
- Các việc M0 (OA test, Zalo App, domain, Gemini key, DN dùng thử)
