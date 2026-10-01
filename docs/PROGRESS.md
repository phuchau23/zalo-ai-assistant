# Tiến độ

## Đang làm
- Module: M1 — Nền tảng
- Task: chờ duyệt kế hoạch M1 bản .NET (chưa code BE)
- Chặn bởi: chủ dự án cài .NET 10 SDK + Docker (Phần A), trả lời Phần B

## Kế hoạch M1 (bản .NET, 2026-10-01) — chờ duyệt
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
- [ ] A2: **.NET 10 SDK** (LTS) — tải ở dotnet.microsoft.com → kiểm tra `dotnet --list-sdks` có dòng `10.x`. Máy đang có 9.0 (hết hỗ trợ 11/2026), giữ song song cũng được. Cài thêm: `dotnet tool install --global dotnet-ef`
- [ ] A3: Docker Desktop — `wsl --install` (restart nếu cần) → cài Docker Desktop (WSL 2) → `docker run --rm hello-world`. Cài vào ổ C (còn ~76 GB, cần ~6–10 GB; ổ D gần đầy)
- [ ] A4: kiểm tra `git --version`
- [ ] A5: tạo repo GitHub cho `zalo-ai-admin` (repo FE) khi muốn push

## Phần B: câu hỏi chờ chủ dự án trả lời (đề xuất của Claude trong ngoặc)
- [ ] B1: duyệt thư viện (duyệt hết)
  - BE (NuGet): Npgsql.EntityFrameworkCore.PostgreSQL, Pgvector.EntityFrameworkCore, EFCore.NamingConventions, Microsoft.EntityFrameworkCore.Design, Hangfire.AspNetCore, Hangfire.PostgreSql, StackExchange.Redis, FluentValidation, Serilog.AspNetCore, Sentry.AspNetCore, Microsoft.Extensions.Http.Resilience, Microsoft.AspNetCore.OpenApi, Microsoft.Extensions.Identity.Core (PasswordHasher)
  - BE test: xunit, Microsoft.AspNetCore.Mvc.Testing, Testcontainers.PostgreSql, Testcontainers.Redis, Shouldly
  - FE: openapi-typescript, openapi-fetch, shadcn/ui (kèm các gói nó cài), vitest
- [ ] B2: đăng ký tài khoản: chỉ super admin tạo tenant qua CLI hay tự đăng ký công khai (chỉ super admin)
- [ ] B3: số chiều embedding (768)
- [ ] B4: schema: `audit_logs.tenant_id` nullable; unique `(tenant_id, external_msg_id)`; session lưu bằng cookie ticket của ASP.NET Core thay vì bảng `sessions` (đồng ý cả 3)
- [ ] B5: tên/ngành 2 tenant mẫu ("Spa Hoa Mai", "Sửa nhà An Phát")

## Đã xong
- [x] Chốt tách 2 repo: BE .NET (`zalo-ai-assistant`) + FE Next.js (`zalo-ai-admin`), queue Hangfire + Postgres (2026-10-01)
- [x] Tạo project FE `C:\Zalo_Tool\zalo-ai-admin` (Next.js 16, TS strict, Tailwind 4, ESLint, App Router, `src/`); lint/typecheck/build pass (2026-10-01)

## Vấn đề mở / nợ kỹ thuật
- Cân nhắc Postgres Row Level Security làm lớp phòng thủ thứ 3 (sau M1)
- Next.js 16 có thay đổi lớn so với bản cũ: đọc `node_modules/next/dist/docs/` trước khi code FE
- Chọn SDK AI .NET (chính thức hay REST) ở M3

## Việc chủ dự án cần làm
- Hoàn thành Phần A và trả lời Phần B ở trên
- Các việc M0 (OA test, Zalo App, domain, Gemini key, DN dùng thử)
