# Tiến độ

## Đang làm
- Module: **M2 — Kho kiến thức: XONG phần code** (2026-10-03). Chờ chủ dự án tự nạp dữ liệu qua giao diện để xác nhận, rồi sang M3
- Task: bước 5–8 xong (embedding Gemini, tài liệu tự do, giao diện Kho kiến thức, chạy thử đầu-cuối), chờ chủ dự án commit + thử trên giao diện; tiếp theo lập kế hoạch M3 (lõi AI + chat thử)
- Chặn bởi: không (Gemini key đã có, đặt bằng user-secrets `Ai:GeminiApiKey`)

## Kế hoạch M2 (2026-10-03) — đã duyệt 2026-10-03
Mở rộng so với CLAUDE.md M2 theo yêu cầu chủ dự án: kiến thức có cấu trúc theo **mẫu chuẩn có Mã** (`docs/KNOWLEDGE-FORMAT.md`) + **so sánh/gộp kiểu review pull request** khi nhập bản mới; tài liệu tự do vẫn nạp được (không so sánh từng mục). Dữ liệu nạp **từ FE**. Ước lượng ~2 tuần.
1. `docs(m2)`: đặc tả mẫu (Excel/JSON, 5 loại mục), câu lệnh nhờ AI ngoài điền mẫu, dữ liệu mẫu Khoa Học Huyệt Đạo ở `C:\Zalo_Tool\sample-docs\` (ngoài repo), đổi tên tenant mẫu
2. `feat(m2)`: bảng `knowledge_items`, `knowledge_imports`, `documents`, `chunks` (vector 768 + HNSW, lọc tenant trước), repository, test cô lập tenant
3. `feat(m2)`: đọc/kiểm lỗi file mẫu (xlsx qua ClosedXML, json), báo lỗi theo sheet + dòng; tải file mẫu trống; xuất dữ liệu hiện tại ra mẫu
4. `feat(m2)`: so sánh (thêm mới / thay đổi từng trường / có thể trùng / không còn trong file / không đổi) → bản xem trước → áp dụng mục được chọn; lịch sử nhập; audit log
5. `feat(m2)`: Gemini embedding (skill `add-ai-provider`, kiểm tra docs chính thức trước), job đánh chỉ mục mục thay đổi, API tìm kiếm `searchChunks(tenantId, query, k)`
6. `feat(m2)`: tài liệu tự do (pdf/docx/xlsx/txt/md ≤ 20MB) lưu file local (dev), job đọc + chia đoạn + embed, thay thế khi trùng tên (hỏi xác nhận), xóa
7. `feat(m2)` (FE): trang Kho kiến thức — danh sách theo loại, nhập file, màn hình so sánh/duyệt, lịch sử, tải mẫu/xuất, tài liệu tự do, ô thử tìm kiếm
8. `test(m2)`: chạy đầu-cuối với dữ liệu mẫu v1 → v2, kiểm tra tiêu chí xong M2
Để sau: hệ thống tự trích PDF lộn xộn sang mẫu bằng AI (cần chat model, M3); so sánh từng dòng cho tài liệu tự do; nơi lưu file production.

## Kế hoạch M1 (bản .NET, 2026-10-01) — đã duyệt 2026-10-02, hoàn thành 2026-10-03
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
- [x] B5: 2 tenant mẫu: "Khoa Học Nguyệt Đạo" (bấm huyệt, massage → slug `spa`, rủi ro Cao) — đổi thành "Khoa Học Huyệt Đạo" (tên thật) ngày 2026-10-03 và "Sửa nhà An Phát" (sửa nhà, rủi ro thấp)

## Đã xong
- [x] **M2 đạt tiêu chí xong** (2026-10-03): chạy thử đầu-cuối qua FE với Gemini thật trên dữ liệu mẫu Khoa Học Huyệt Đạo — 6/6 câu hỏi mẫu ra đúng mục ở vị trí đầu (ví dụ "giá massage đông y 60 phút" → DV-MASSAGE-DONG-Y-60), nhập v2 ra đúng bản so sánh mong đợi, tài liệu tự do đọc + tìm được; tenant B không tìm thấy dữ liệu A (test). Còn: chủ dự án tự nạp qua giao diện, và thử với dữ liệu thật của DN dùng thử khi có
- [x] M2 bước 8: chạy thử đầu-cuối (script qua FE, Gemini thật, tenant An Phát rồi dọn sạch); sửa OpenAPI sinh `number | string` (JSON chỉ nhận số thật) (2026-10-03)
- [x] M2 bước 7 (FE): trang Kho kiến thức — tab Dữ liệu (lọc, xem chi tiết), Nhập file (tải mẫu, xuất Excel/JSON, lỗi theo dòng, câu lệnh nhờ AI có nút sao chép), Lịch sử nhập, Tài liệu tham khảo (thay thế khi trùng tên, xóa, tự cập nhật trạng thái), Thử tìm kiếm; trang duyệt so sánh `/knowledge/imports/[id]` (chọn từng mục, gộp/giữ cả hai/bỏ qua, khóa "vừa gộp vừa xóa", xác nhận trước khi áp dụng/hủy); 23 test FE (2026-10-03)
- [x] M2 bước 6: tài liệu tự do PDF/Word/Excel/TXT/MD ≤ 20MB (kiểm nội dung khớp đuôi file, tên file an toàn, lưu local `%LOCALAPPDATA%zaloaiiles`), job đọc + chia đoạn ~1.500 ký tự chồng lấn ~15% theo mục + tạo vector, PDF ảnh scan/file hỏng → "failed" kèm lý do, thay thế khi trùng tên (409 document_exists), xóa (2026-10-03)
- [x] M2 bước 5: `IEmbeddingProvider` + Gemini `gemini-embedding-2` 768 chiều qua REST (resilience: timeout, retry, circuit breaker; lỗi → AiProviderException → API 503 ai_unavailable), provider giả cho test; đánh chỉ mục sau khi áp dụng + job quét 5 phút/lần; `POST /knowledge/search`, `GET /knowledge/status`, `GET /knowledge/ai-prompt`; tổng 156 test BE (2026-10-03)
- [x] M2 bước 4: so sánh file nhập với dữ liệu đang có (thêm mới / thay đổi từng trường / có thể trùng — bỏ dấu, số phải khớp / không còn trong file), bản xem trước lưu `knowledge_imports`, áp dụng mục được chọn trong 1 transaction (gộp vào mục cũ hoặc giữ cả hai; chặn vừa gộp vừa xóa; từ chối nếu mục bị sửa trong lúc chờ duyệt), hủy, lịch sử, `GET /knowledge/items`; mục đổi/thêm tạo đoạn chờ đánh chỉ mục; lệnh `knowledge diff`; dữ liệu mẫu v1→v2 cho kết quả đúng README; tổng 123 test (2026-10-03)
- [x] M2 bước 3: đọc file mẫu Excel (ClosedXML) + JSON, kiểm lỗi từng ô báo theo sheet/dòng/cột (dễ dãi: tiêu đề khác hoa thường, giá "450.000đ", dòng trống), tạo file mẫu trống + xuất dữ liệu (`GET /knowledge/template`, `GET /knowledge/export`), lệnh `knowledge validate|convert`; dữ liệu mẫu có thêm bản .xlsx; tổng 108 test (2026-10-03)
- [x] M2 bước 2: migration `AddKnowledgeBase` (knowledge_items, knowledge_imports, documents, chunks vector(768) + HNSW cosine, ràng buộc "đúng một nguồn"), 4 repository có tenant, tìm kiếm vector lọc tenant_id + `hnsw.iterative_scan`; 8 test cô lập (kể cả tìm kiếm vector, chuyển dòng sang tenant khác); tổng 85 test (2026-10-03)
- [x] M2 bước 1: `docs/KNOWLEDGE-FORMAT.md` (mẫu dữ liệu 5 loại mục, quy tắc Mã, JSON, luồng so sánh/gộp, câu lệnh nhờ AI điền); dữ liệu mẫu Khoa Học Huyệt Đạo v1 (71 mục) + v2 giả lập (để test gộp) + 1 tài liệu tự do ở `C:Zalo_Toolsample-docs`; đổi tenant mẫu thành "Khoa Học Huyệt Đạo" (seed, test, DB dev) (2026-10-03)
- [x] M1 bước 8: tách 7 skill BE ra `.claude/skills/` (bổ sung đường dẫn code thật), thêm 4 skill FE ở `zalo-ai-portal/.claude/skills/` (portal-page, portal-form, api-client-sync, portal-ui dùng `ui-ux-pro-max` — bản MIT đã rà soát, copy vào repo FE, chạy không cần Python), khung `docs/zalo-api-notes.md` (mọi mục CHƯA KIỂM CHỨNG, làm đầu M4), CLAUDE.md mục 10 thành bảng trỏ tới skill, tick task M1 (2026-10-03)
- [x] **M1 đạt tiêu chí xong**: docker compose + api + worker + FE chạy, đăng nhập admin, test cô lập tenant pass, CI 2 repo (2026-10-03)
- [x] M1 bước 7: Sentry (Sentry.AspNetCore cho Api + Sentry.Serilog cho cả Api và Worker, vùng EU, che SĐT/email và bỏ query/cookie/header trước khi gửi), `POST /dev/errors/test`; `scripts/backup-db.cmd` + `restore-db.cmd` (đã thử khôi phục vào DB tạm); CI GitHub Actions cả 2 repo; `docs/OPERATIONS.md` (2026-10-03)
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
- Deploy: cấu hình ForwardedHeaders sau reverse proxy, nếu không rate limit login tính theo IP của proxy (gộp mọi khách)
- Đăng xuất chưa thu hồi cookie phía server: cookie bị đánh cắp còn dùng được tới khi hết hạn (12h trượt). Xóa user/membership thì mất quyền ngay. Cân nhắc thêm "security stamp" trên users (đổi mật khẩu, đăng xuất mọi nơi)
- Super admin xem/chuyển sang tenant bất kỳ: làm ở M6
- FE chưa có test giao diện (chỉ test hàm thuần bằng vitest). Cân nhắc Playwright cho luồng đăng nhập ở tuần 7–8 (thư viện mới, cần duyệt)
- `pnpm gen:api` cần BE đang chạy. Có thể cho BE xuất file openapi lúc build (thêm gói Microsoft.Extensions.ApiDescription.Server) nếu bất tiện
- Cảnh báo job lỗi: đã gửi Sentry (bước 7), thêm Telegram ở M5
- File gốc tài liệu lưu local (`Storage:LocalRoot`), chưa nằm trong `scriptsbackup-db.cmd`; production cần object storage + backup riêng (chốt khi deploy)
- Chưa ghi chi phí gọi embedding (`usage_records` làm ở M3); gói Gemini miễn phí chỉ dùng cho dev
- Log console: message của exception (ví dụ lỗi từ Zalo/AI trả về) chưa được che PII (Sentry thì đã che từ bước 7) — xử lý khi viết ZaloClient (M4) và AI provider (M3)
- Chưa có form thêm/sửa từng mục (FAQ, dịch vụ) trên giao diện — hiện sửa qua xuất → sửa file → nhập. Làm nếu DN dùng thử thấy bất tiện
- Cân nhắc Postgres Row Level Security làm lớp phòng thủ thứ 3 (sau M1)
- Next.js 16 có thay đổi lớn so với bản cũ: đọc `node_modules/next/dist/docs/` trước khi code FE
- Khoa Học Huyệt Đạo (phòng khám có thật, khoahochuyetdao.com): website quảng cáo trị liệu bệnh (u xơ tử cung, nang ngực, tim, tuyến giáp, liệt dây VII...), châm cứu, "không tác dụng phụ", "giảm đau buổi đầu hoặc hoàn tiền" → trước khi chạy thật với khách của họ: hỏi luật sư, xác minh giấy phép; hiện chỉ dùng làm dữ liệu test. Nguyên tắc chung: nếu DN quảng cáo chữa bệnh (châm cứu, trị liệu YHCT) → thường cần giấy phép, thành ngành "chưa mở", hỏi luật sư. Chỉ massage/bấm huyệt thư giãn thì xếp `spa`
- Chọn SDK AI .NET (chính thức hay REST) ở M3
- Xác minh ở Phase 4 (docs Zalo): thời gian OA được nhắn tư vấn sau tin cuối của khách; webhook có sự kiện "OA gửi tin" khi nhân viên trả lời trong app Zalo không

## Việc chủ dự án cần làm
- Đặt Sentry DSN (`dotnet user-secrets set "Sentry:Dsn" ...`), bật branch protection cho `dev`/`main` ở cả 2 repo (docs/OPERATIONS.md mục 3)
- Khi làm hồ sơ pháp lý: Sentry (EU) là bên nhận dữ liệu ở nước ngoài
- Chọn ngành đầu tiên từ `docs/INDUSTRIES.md`; xem lại các mục "(đề xuất)" trong `docs/FEATURE-SPECS.md`
- Các việc M0 còn lại: OA test, Zalo App, domain, liên hệ DN dùng thử (Gemini key: xong)
