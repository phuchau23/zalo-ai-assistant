# Quyết định kỹ thuật

Mỗi quyết định: ngày, nội dung, lý do, hệ quả.

## 2026-10-01 — Tách 2 repo: BE .NET + FE Next.js

**Quyết định:** Bỏ monorepo pnpm (Node.js/Fastify/BullMQ/Drizzle). Dự án gồm 2 repo nằm cạnh nhau trong `C:\Zalo_Tool\`:
- `zalo-ai-assistant`: backend **.NET 10** (ASP.NET Core API + Hangfire Worker + EF Core). Giữ CLAUDE.md và `docs/` làm nguồn chính.
- `zalo-ai-admin`: frontend **Next.js** (App Router, TypeScript, Tailwind, shadcn/ui).

**Lý do:** chủ dự án quen C#/.NET hơn Node, nên tự đọc, sửa, vận hành BE được.

**Hệ quả:**
- Không dùng chung type giữa FE/BE. BE xuất OpenAPI, FE sinh client bằng `openapi-typescript` (`pnpm gen:api`). Đổi API thì phải sinh lại và commit ở cả 2 repo.
- Hàng đợi dùng **Hangfire + PostgreSQL storage** (không dùng Redis cho queue): đơn giản, có dashboard và retry sẵn. Redis vẫn dùng cho lock refresh token, OAuth state, dedupe, rate limit gửi.
- Cô lập tenant có 2 lớp: repository nhận `tenantId` + EF Core global query filter.
- Mật khẩu hash bằng `PasswordHasher` của ASP.NET Core Identity (PBKDF2) thay vì argon2, để không thêm thư viện ngoài. Đổi sang argon2 sau được (hash cũ vẫn verify được nếu lưu kèm phiên bản thuật toán).
- FE gọi BE qua rewrite `/api/*` của Next.js để cookie session cùng domain.
- Kế hoạch M1 cũ (8 bước, pnpm) bị thay thế. Câu hỏi B1 cũ (thư viện Node) không còn áp dụng.
- SDK AI cho .NET: dùng SDK chính thức nếu ổn định, không thì gọi REST. Chốt ở M3.
