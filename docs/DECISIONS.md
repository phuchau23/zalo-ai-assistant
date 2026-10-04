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

## 2026-10-02 — Trang quản trị FE (M1 bước 6)

- **Gọi BE qua rewrite** `/api/*` → `API_INTERNAL_URL` (và `/hangfire/*` cho dashboard). Trình duyệt gọi cùng domain nên cookie httpOnly hoạt động mà không cần CORS.
- **Lấy dữ liệu ở trình duyệt** (client component + `openapi-fetch`), không qua server component, để không phải chuyển tiếp cookie thủ công từ server Next.js sang BE. Xem lại khi cần SEO hoặc tối ưu tải trang (trang quản trị không cần SEO).
- **`proxy.ts`** (Next.js 16 đổi tên middleware → proxy) chỉ kiểm có cookie hay chưa; không chuyển hướng ngược từ `/login` khi có cookie, để cookie cũ/bị thu hồi không gây vòng lặp. BE trả 401 → FE về `/login?next=...`; `next` được lọc chống open redirect.
- **shadcn/ui** (style `radix-nova`, Radix UI). Bản mới dùng gói `cn` (của chính tác giả shadcn, thay clsx + tailwind-merge) — đã kiểm tra người duy trì và repo trước khi giữ.
- Lỗi validate từ BE trả tiếng Việt (FluentValidation culture `vi`, tên trường đặt bằng `WithName`); FE hiện lỗi dưới từng ô theo key camelCase.

## 2026-10-03 — Sentry, backup, CI (M1 bước 7)

- **Sentry một nguồn:** lỗi đi qua Serilog → `Sentry.Serilog` (cả Api và Worker), nên đã qua bộ che log. `Sentry.AspNetCore` ở Api chỉ để gắn ngữ cảnh request; tắt kênh log riêng của nó (`MinimumEventLevel = None`) để không gửi trùng. Worker để Serilog khởi tạo SDK.
- **Che trước khi gửi** (`SentryScrubber`, BeforeSend/BeforeBreadcrumb): che SĐT/email trong message và exception, bỏ query string (callback OAuth có `code`), cookie, header, body; `SendDefaultPii = false`. Breadcrumb bỏ phần data.
- **Vùng dữ liệu EU** (`*.ingest.de.sentry.io`). Chỉ bật Error monitoring; không bật Logs/Tracing/Metrics của Sentry để hạn chế dữ liệu ra nước ngoài.
- **Backup dev** bằng `pg_dump -Fc` trong container, chép ra `backups\`, giữ 14 bản. Production: backup của nhà cung cấp + bản `pg_dump` hằng ngày ở nơi khác + thử khôi phục hằng tháng (`docs/OPERATIONS.md`).
- **CI:** GitHub Actions ở cả 2 repo, chạy khi push `main`/`dev` và mọi PR; BE chạy test tích hợp với Docker có sẵn trên runner ubuntu.

## 2026-10-03 — Kho kiến thức có cấu trúc + so sánh/gộp (M2, chủ dự án duyệt)

**Bối cảnh:** chủ dự án muốn doanh nghiệp tự nạp dữ liệu từ giao diện, và khi doanh nghiệp gửi bộ dữ liệu "chuẩn" sau này thì giao diện phải chỉ ra chỗ khác nhau và cho duyệt gộp (như merge code), tránh hai tài liệu cùng chủ đề nhưng khác thông tin "đá nhau".

**Quyết định:**
- Kiến thức chính nạp bằng **mẫu chuẩn** (Excel/JSON) gồm 5 loại mục (thông tin chung, dịch vụ, gói, FAQ, chính sách), **mỗi mục có Mã ổn định** → so sánh theo Mã được tới từng trường. Đặc tả: `docs/KNOWLEDGE-FORMAT.md`.
- Nhập file → **bản xem trước** (thêm mới / thay đổi / có thể trùng / không còn trong file / không đổi) → người dùng chọn → áp dụng. Mục "không còn trong file" mặc định **giữ lại**; "có thể trùng" (mã khác, tên gần giống) phải chọn tay. Mỗi lần nhập lưu lịch sử + audit log.
- Doanh nghiệp tự điền mẫu, hoặc dùng **câu lệnh mẫu cho AI bên ngoài** (ChatGPT/Claude/Gemini) để chuyển tài liệu lộn xộn → JSON. Hệ thống tự trích bằng AI để sang M3 (cần chat model).
- Tài liệu tự do (PDF/Word/...) vẫn nạp được làm "tài liệu tham khảo", không so sánh từng mục; trùng tên → hỏi rồi thay thế.
- Đơn giá lưu số nguyên VNĐ; `price = null` + `priceNote` khi không công bố giá (bot phải nói "liên hệ", không đoán).

**Dữ liệu mẫu:** lấy từ website công khai của phòng khám có thật "Khoa Học Huyệt Đạo" (đổi tên tenant mẫu cho đúng), giữ nguyên các câu quảng cáo chữa bệnh/cam kết để test bộ lọc ở M3. Lưu ngoài repo. Phòng khám này quảng cáo trị liệu bệnh → trước khi phục vụ thật: hỏi luật sư, xác minh giấy phép (INDUSTRIES.md "chưa mở").

## 2026-10-03 — Schema kho kiến thức (M2 bước 2)

- **Embedding:** `gemini-embedding-2` (docs chính thức https://ai.google.dev/gemini-api/docs/embeddings, kiểm tra 2026-10-03): đa ngôn ngữ (có tiếng Việt), 768 chiều là mức khuyến nghị, tự chuẩn hóa vector ở số chiều không mặc định; không dùng tham số `task_type` (ghi nhiệm vụ vào nội dung). Tên model đặt trong config `Ai:EmbedModel`. Cột `chunks.embedding` = `vector(768)` cố định → `Ai:EmbedDim` phải bằng 768.
- **Bảng:** `knowledge_items` (mục có cấu trúc, `data` jsonb, mã không trùng trong tenant), `knowledge_imports` (bản xem trước + lịch sử nhập), `documents` (tài liệu tự do, tên file không trùng trong tenant), `chunks` (đúng một nguồn: mục hoặc tài liệu; xóa nguồn → xóa đoạn).
- **Không mã hóa** nội dung kiến thức: là thông tin công khai của doanh nghiệp. Dữ liệu cá nhân (tin nhắn khách) vẫn mã hóa từ M3.
- **Tìm kiếm vector:** SQL thô có `WHERE tenant_id = …` trước `ORDER BY embedding <=> …`, bật `SET LOCAL hnsw.iterative_scan = relaxed_order` (pgvector 0.8.7) để chỉ mục HNSW không trả thiếu kết quả khi nhiều tenant dùng chung bảng; sắp lại theo khoảng cách ở code.
- Core giữ `float[]` cho embedding (không phụ thuộc thư viện pgvector), Infrastructure chuyển sang kiểu `vector`.

## 2026-10-03 — So sánh và gộp khi nhập file (M2 bước 4)

- **Phân quyền:** staff xem kho kiến thức, bản so sánh, lịch sử; **chỉ owner** tạo bản so sánh, áp dụng, hủy (dữ liệu bot dùng để báo giá cho khách). Đổi được sau nếu DN muốn giao cho nhân viên.
- **"Có thể trùng":** chỉ xét mục cùng loại, mã khác; so tên sau khi bỏ dấu + chữ thường, **các con số phải khớp tuyệt đối** (Massage 60' ≠ Massage 90'), độ giống Levenshtein ≥ 0,85. Mặc định không chọn; người dùng bắt buộc chọn "gộp vào mục cũ" (giữ mã cũ, lấy nội dung mới) hoặc "giữ cả hai".
- **An toàn khi áp dụng:** một transaction; mỗi mục cũ phải còn đúng dấu vân tay (`content_hash`) như lúc so sánh, nếu không → 409, nhập lại file; không cho vừa gộp vào vừa xóa cùng một mục; lần nhập đã áp dụng/hủy không áp dụng lại được.
- Bản so sánh lưu cả nội dung mới của từng mục → áp dụng không cần tải lại file. Mục thêm/đổi/gộp có đoạn (chunk) mới chưa có vector; bước 5 đánh chỉ mục.
- Upload multipart không dùng antiforgery token (dựa vào cookie SameSite=Lax như các API khác); giới hạn 5 MB/file mẫu.

## 2026-10-03 — Đánh chỉ mục, tài liệu tự do, giao diện kho kiến thức (M2 bước 5–8)

- **Embedding tách khỏi chat:** interface `IEmbeddingProvider` (Core) riêng với chat (M3), vì `Ai:EmbedProvider` và `Ai:ChatProvider` cấu hình độc lập. Gemini gọi REST `batchEmbedContents` (định dạng kiểm chứng từ docs chính thức 2026-10-03): key ở header `x-goog-api-key` (không trên URL), `output_dimensionality` 768, nhiệm vụ ghi vào nội dung (`title: … | text: …` cho tài liệu, `task: search result | query: …` cho câu hỏi). Lỗi 429 → `AiRateLimitedException`; lỗi khác không đưa body phản hồi vào message (có thể lặp lại nội dung khách).
- **`Ai:EmbedProvider = fake`**: vector giả tính tại chỗ (bag-of-words) cho test/CI, không gọi mạng. Khởi động kiểm tra: `EmbedDim` phải 768; dùng gemini thì bắt buộc có key.
- **Đánh chỉ mục bất đồng bộ:** áp dụng bản nhập → xếp `IndexKnowledgeJob` cho tenant; job quét `IndexSweepJob` mỗi 5 phút làm lưới an toàn (truy vấn hệ thống `IgnoreQueryFilters` chỉ đọc tenant_id). Mỗi lô 50 đoạn lưu ngay để lỗi giữa chừng không mất phần đã làm.
- **Tài liệu tự do:** ≤ 20MB; kiểm nội dung khớp đuôi (PDF `%PDF`, docx/xlsx `PK`, văn bản không có byte 0); tên hiển thị giữ tiếng Việt nhưng tên trên đĩa chỉ ASCII; lưu local `%LOCALAPPDATA%\zaloai\files` (Api và Worker cùng máy) — production phải dùng object storage. Chia đoạn ~1.500 ký tự, chồng lấn ~220 ký tự, đoạn mới ở mỗi tiêu đề mục. File hỏng / PDF ảnh scan → `failed` kèm lý do, không thử lại; lỗi AI → thử lại.
- **Câu lệnh nhờ AI** là hằng số trong code (`KnowledgeTemplate.AiPrompt`), hiện trên giao diện có nút sao chép; bản trong docs phải giữ giống.
- **JSON API chỉ nhận số thật** (`NumberHandling = Strict`): OpenAPI của .NET 10 mặc định sinh `number | string` cho trường số, làm type FE khó dùng.
- **Giao diện duyệt:** logic chọn (mặc định, khóa "vừa gộp vừa xóa", đếm thay đổi) nằm trong `selection.ts` có test riêng; áp dụng/hủy luôn hỏi xác nhận; mục "không còn trong file" hiện nhãn "Sẽ xóa" khi được chọn.

## 2026-10-04 — Lõi AI + chat thử (M3)

- **Chỉ dùng Gemini, bỏ Claude** (chủ dự án chốt 2026-10-04). Chat `gemini-3.8-flash`, dự phòng `gemini-3.5-flash-lite` khi model chính quá tải (503) sau 2 lần thử lại. Gọi REST `generateContent` (định dạng kiểm chứng bằng request thật 2026-10-04): JSON ép bằng `generationConfig.responseMimeType` + `responseJsonSchema` (`response_format` bị từ chối). Token "suy nghĩ" tính như output. Giá đặt ở `Ai:Pricing` (appsettings) theo trang giá Google, cập nhật tay. `Ai:ChatProvider = fake` cho test/CI. **Gói Gemini miễn phí chỉ dùng cho dev**: Google được dùng dữ liệu gói free để cải thiện sản phẩm → production bắt buộc gói trả phí.
- **Phân lớp:** contract ở Core (`IChatProvider`, `IKnowledgeSearch`, `IBotEngine`, `IChannelAdapter`), bộ não `BotEngine` ở ZaloAi.Ai (không đụng database), job `ProcessIncomingMessageJob` ở Infrastructure (đọc/ghi DB, mã hóa, gọi bộ não, gửi qua adapter). Webchat (chat thử) và Zalo (M4) dùng chung job.
- **Thứ tự an toàn trong một lượt:** che dữ liệu cá nhân (SĐT VN, email, CCCD 12 số, CMND 9 số chỉ khi có chữ "CMND" gần đó, tên khách đã biết) → kiểm dấu hiệu nguy hiểm bằng từ khóa (bỏ dấu, không gọi AI) → tìm kiến thức bằng câu đã che (độ giống ≥ 0,35, tối đa 6 đoạn) → AI trả JSON → sai định dạng thử lại 1 lần → câu cấm thử viết lại 1 lần → có giá mà không dẫn đoạn dữ liệu nào thì chặn (0 bịa giá) → `confidence = low` thì chuyển người → câu hỏi sức khỏe tự thêm lời khuyên thăm khám → ghép lại dữ liệu đã che → tin đầu tiên có câu báo trợ lý AI + link chính sách (không tắt được). Lỗi AI → câu dự phòng + chuyển người, không im lặng.
- **Câu cấm:** so khớp có dấu ("không đau" ≠ "không đâu"); `strict` = luôn cấm (cam kết kết quả y khoa), không strict = được dùng khi đúng nguyên văn đoạn dữ liệu bot đã dùng. Bỏ các cụm quá chung ("đảm bảo", "chắc chắn", "anh bị", "ngưng thuốc") vì chặn nhầm câu vô hại — phần đó do quy tắc + eval lo.
- **Dấu hiệu nguy hiểm:** so khớp bỏ dấu (khách hay gõ không dấu), chỉ dùng cụm đủ cụ thể: "ngất" → "bị ngất"/"ngất xỉu" (tránh "ngắt"), "xỉu" → "bị xỉu" (tránh "xíu"). AI còn tự đánh giá `urgency` (lớp 2).
- **Cờ `medically_reviewed`** trên mục kiến thức và tài liệu, owner bật/tắt (audit). Sửa nội dung → tự bỏ cờ. Prompt ghi nhãn từng đoạn "đã/chưa duyệt chuyên môn".
- **Hội thoại:** bảng `contacts`, `conversations`, `messages` (nội dung mã hóa, `ai_trace` jsonb không chứa nội dung/PII), `usage_records`. Mỗi tin khách chỉ có 1 câu trả lời (unique `reply_to_message_id`) → chạy lại job không gửi trùng. Khách nhắn liên tiếp → chỉ trả lời tin cuối. Chuyển người → `mode = human`, bot im. Hội thoại dài → tóm tắt phần cũ (giữ 10 tin gần nhất nguyên văn), tóm tắt lưu mã hóa.
- **Hàng đợi `messages`** ưu tiên trước `default` (đánh chỉ mục, đọc tài liệu) để khách không phải chờ việc nền.
- **Chat thử:** chỉ thao tác hội thoại `is_test` của tenant hiện tại; giới hạn 20 tin/phút/người dùng (mỗi tin tốn tiền AI). Giọng văn (thân thiện/chuyên nghiệp/ngắn gọn) + hướng dẫn thêm ≤ 1000 ký tự, đặt SAU quy tắc hệ thống và ghi rõ không ghi đè được an toàn.
- **Eval:** `evals.json` trong mẫu ngành; lệnh `dotnet run --project src/ZaloAi.Api -- eval` chạy trên kho kiến thức thật bằng AI thật; "bịa giá" = số tiền trong câu trả lời không có trong dữ liệu DN. Ngưỡng: thường ≥ 85%, 0 bịa giá, an toàn 100%.

## 2026-10-04 — Kết nối Zalo OA (M4)

- **Nguồn API:** mọi chi tiết Zalo (OAuth v4 + PKCE, token 25 giờ / refresh 3 tháng dùng 1 lần, webhook + chữ ký, gửi tin tư vấn, mã lỗi) lấy từ docs chính thức do chủ dự án chụp/copy, ghi ở `docs/zalo-api-notes.md` kèm ngày. Trang docs developers.zalo.me hiển thị bằng JS + Cloudflare nên Claude không tự đọc được.
- **Phụ thuộc:** Infrastructure tham chiếu Channels (để lưu token/khóa/mã hóa quanh `ZaloClient`). Channels vẫn chỉ phụ thuộc Core, chỉ lo giao thức Zalo (HTTP, chữ ký, PKCE, đọc sự kiện). Không có vòng lặp.
- **Redis** (`StackExchange.Redis`, đã duyệt ở M1) qua `IDistributedStore`: khóa làm mới token theo kết nối (Lua so-khớp để không nhả khóa người khác), OAuth state 10 phút dùng 1 lần (GETDEL), chống trùng webhook theo `oa_id:msg_id` 1 ngày, đếm giới hạn gửi theo OA/phút. Test bằng `Testcontainers.Redis`.
- **Một OA chỉ thuộc một DN** (unique `channel + external_id` toàn hệ thống): webhook tìm DN theo OA ID phải ra đúng 1. DN khác kết nối OA đang dùng → báo "oa_in_use", không lộ DN nào. Ngắt kết nối = xóa dòng (xóa luôn token), hội thoại giữ lại (`connection_id` → null).
- **Callback OAuth không đăng nhập:** tenant + user + code_verifier lấy từ state trong Redis, không từ query. Lỗi → chuyển về trang Kênh với `?error=<mã ngắn>`. URL callback có `code`: request log chỉ ghi path.
- **Webhook:** đọc nguyên body → kiểm `X-ZEvent-Signature` (sha256(appId + body + timestamp + OA Secret Key)), chấp nhận có/không tiền tố `mac=` và hex hoa/thường vì docs chưa rõ (ghi log *định dạng* header khi sai để đối chiếu với Zalo thật) → đúng app → chống trùng → lưu tin mã hóa → xếp job → 200. Tin bot gửi qua API dội về (`oa_send_*` không có `admin_id`) bị bỏ qua; có `admin_id` = nhân viên trả lời trong app OA → hội thoại chuyển `human`, bot im.
- **Không tự retry POST tới Zalo ở tầng HTTP** (`DisableForUnsafeHttpMethods`): refresh token chỉ dùng 1 lần, gửi lại tin = khách nhận 2 lần. Job thử lại có kiểm tra trạng thái: tin bot lưu `pending` trước khi gửi; chạy lại thì gửi lại đúng tin đó (không gọi AI lần 2). Còn rủi ro nhỏ: Zalo đã nhận tin nhưng mất phản hồi → gửi lại 1 lần.
- **Phân loại lỗi gửi:** -216/-220 → làm mới token rồi gửi lại 1 lần; -223/-219/-209/-212/-204/-205 → kết nối `needs_reauth` (log Error → Sentry); -230/-232/-227/-213/-244/-218 → tin `failed`, không gửi lại; -32/-200/5xx/mạng → job thử lại.
- **Tin không phải chữ** (ảnh, video, file, thoại, vị trí): lưu dạng "[Khách gửi hình ảnh]", bot trả lời cố định + chuyển nhân viên, không gọi AI (không bịa nội dung ảnh). Sticker: đáp ngắn, không chuyển người.
- **Không gọi AI** khi kênh Zalo của hội thoại đã ngắt hoặc cần cấp quyền lại.
- **Làm mới token:** job quét 30 phút/lần, token còn < 2 giờ thì xếp job làm mới riêng cho từng kết nối (trong đúng tenant). Ngoài ra làm mới ngay khi gửi tin gặp -216/-220.
- **Quyền app xin OA cấp** tối thiểu cho GĐ1: gửi tin, thông tin OA, tin nhắn người dùng; webhook tin nhắn + người dùng. ZNS/Template bật lại ở GĐ2 (OA phải cấp quyền lại).

## 2026-10-04 — Hộp thư + tiếp quản (M5)

- **Hộp thư hiện cả hội thoại Chat thử** (nhãn "Chat thử"): chủ dự án/DN test trọn luồng khách ↔ bot ↔ nhân viên trên web, không cần Zalo. Báo cáo/hạn mức (M6) vẫn loại hội thoại thử. Chat thử cho "khách" nhắn khi nhân viên đang xử lý (trước đây bị chặn 409).
- **"Cần bạn"** = `conversations.needs_attention_since` (bot chuyển người, hoặc khách nhắn khi nhân viên đang xử lý); xóa khi nhân viên trả lời / tiếp quản có câu giới thiệu / trả lại bot. Danh sách xếp ca chờ lâu nhất lên đầu.
- **Câu chuyển tiếp là tin `system`** (lưu, gửi qua kênh như tin thường): chuyển người trong/ngoài giờ (luôn bật, trừ ca khẩn cấp vì câu khẩn cấp đã đủ), tiếp quản (mặc định bật), trả lại bot (mặc định tắt), chữ ký "— Tên, CSKH" (mặc định bật). Cài đặt 1 dòng/tenant `handoff_settings`, giờ Việt Nam.
- **Nhân viên gửi tin = tự tiếp quản** (bot im, tự gán cho người gửi nếu chưa ai phụ trách). Tin nhân viên/hệ thống lưu `pending` rồi job `SendOutgoingMessageJob` gửi (API trả nhanh, mạng lỗi có thử lại).
- **Tự chuyển người thêm:** sentiment âm → chuyển ("negative_sentiment"). "Không chắc liên tiếp" không cần riêng vì bot đã chuyển ngay từ lần không chắc đầu tiên (M3).
- **Realtime:** SSE `/inbox/stream`, sự kiện qua Redis pub/sub kênh theo tenant, chỉ chứa {type, conversationId} — trình duyệt tự tải lại qua API có kiểm quyền. FE dự phòng tải lại 20 giây/lần nếu SSE rớt.
- **Nhắc khi chờ lâu:** job hệ thống mỗi phút (IgnoreQueryFilters, chỉ đọc id + mốc giờ) → job theo tenant ghi `last_reminder_at`, báo hộp thư, Telegram; lặp mỗi X phút (5–240, mặc định 10) tới khi có người trả lời.
- **Telegram** (Bot API sendMessage, HttpClient, không thư viện): một bot của hệ thống, mỗi DN điền chat id nhóm. **Không gửi nội dung tin / thông tin cá nhân của khách** sang Telegram (bên thứ ba) — chỉ loại sự việc, tên DN, lý do, link hộp thư.
- **Audit xem hội thoại:** ghi `conversation.viewed`, tối đa 1 lần / người / hội thoại / 30 phút (khóa Redis) để không ngập log khi giao diện tải lại.
- **Vai trò:** staff vào thẳng Hộp thư; owner có Tổng quan (số khách đang chờ). Chỉ owner gán người phụ trách và sửa cài đặt chuyển tiếp; staff tiếp quản/trả lời được.

## 2026-10-04 — Khách hàng + Cần chăm sóc + kết nối Telegram (M6 đợt A)

- **5 tầng khách** (chủ dự án chốt): Mới → Quan tâm → Nóng → Đã chốt → Không tiềm năng. Hệ thống **chỉ tự nâng**, không bao giờ hạ: để lại SĐT hoặc muốn đặt lịch → Nóng; nói rõ nhu cầu (dịch vụ, thời gian, chi nhánh) hoặc nhắn ≥ 3 tin → Quan tâm; AI "Cần chăm sóc" đánh giá nóng/ấm → Nóng/Quan tâm. Đã chốt / Không tiềm năng chỉ nhân viên đặt. Nhân viên đặt tay → `lead_status_manual`, hệ thống không đụng nữa cho tới khi chọn lại "Tự động". Quy tắc rõ ràng, không thêm trường vào JSON trả lời của bot (không phải chạy lại eval).
- **Nhật ký chăm sóc** (`contact_notes`): nhân viên ghi khách đã làm gì + "ngày chăm sóc lại". Nội dung mã hóa (có thể chứa thông tin sức khỏe). Tới ngày → tạo gợi ý "Tới ngày hẹn chăm sóc". Đây là dữ liệu đầu vào cho flow tự động GĐ2.
- **"Cần chăm sóc" chỉ GỢI Ý, không tự gửi.** Nhân viên sửa tin nháp rồi bấm gửi (đi qua hộp thư như tin nhân viên → hội thoại chuyển sang nhân viên). Mỗi khách tối đa 1 gợi ý đang mở (unique có điều kiện), phân tích lại thì cập nhật. Khách nhắn lại → gợi ý tự "đã xử lý" (`customer_replied`). Quá hạn nhắn → "hết hạn".
- **Khi nào AI phân tích:** job hệ thống 15 phút/lần; khách im lặng ≥ X giờ (cài đặt, mặc định 6, 1–72), còn trong 7 ngày Zalo cho nhắn, chưa phân tích từ tin mới nhất (`conversations.care_analyzed_at`), không có khách đang chờ người; tối đa 100 lần gọi AI/lượt; DN tắt được. Nhân viên bấm "AI gợi ý chăm sóc" thủ công: 1 lần / 2 phút / hội thoại. **Khác đặc tả:** đặc tả nói "không phân tích hội thoại mode = human"; đổi thành "không phân tích khi khách đang chờ người" — vì sau khi nhân viên tiếp quản, hội thoại thường ở mode human mãi, nếu theo đặc tả thì khách đó không bao giờ được gợi ý chăm sóc.
- **Hạn nhắn Zalo:** `messaging_deadline` = tin cuối của khách + 7 ngày (docs Zalo, `zalo-api-notes.md`); giao diện ghi thêm "miễn phí 48 giờ đầu, sau đó có thể tính phí". Quá hạn → khóa "Nhắn ngay", gợi ý gọi điện (ZNS ở GĐ2). Chat thử không giới hạn.
- **Tin nháp an toàn:** che PII trước khi gửi AI (cả ghi chú nhân viên), prompt cấm bịa giá/ưu đãi/lịch (để chỗ trống cho nhân viên điền), tin nháp có câu cấm của ngành → bỏ tin nháp. Lý do/gợi ý/tin nháp lưu mã hóa.
- **Danh sách khách:** tìm tên/SĐT trong bộ nhớ sau khi giải mã (tối đa 5.000 khách) — không lưu SĐT dạng thường để tìm. Xuất Excel chỉ owner, ghi audit `contacts.exported`, mặc định không gồm khách chat thử, ô luôn là chữ (không chạy công thức). Xem hồ sơ ghi audit `contact.viewed` (1 lần/người/khách/30 phút).
- **Kết nối Telegram tự phục vụ:** owner lấy mã 6 ký tự (bỏ chữ dễ nhầm, Redis 10 phút, dùng 1 lần) → nhóm gõ `/ketnoi@tên_bot MÃ` (bot chế độ riêng tư chỉ nhận lệnh có @tên_bot) hoặc bấm link `t.me/<bot>?startgroup=MÃ`. Worker đọc tin bằng **long polling `getUpdates`** (không cần URL công khai như webhook; chạy được cả ở máy dev), chỉ một Worker đọc tại một thời điểm (khóa Redis). Lưu chat id vào đúng DN theo mã, gửi tin chào vào nhóm, audit `tenant.telegram_connected`. Không ghi log nội dung tin trong nhóm. Ô chat id thủ công vẫn giữ (ẩn trong "Nhập thủ công").
- **Lịch chăm sóc lại theo giờ** (chủ dự án yêu cầu 2026-10-04): `contact_notes.follow_up_at` (timestamptz, lưu UTC; giao diện nhập/hiện giờ Việt Nam) thay cho chỉ ngày — migration `NoteFollowUpTime`, lịch cũ chỉ có ngày chuyển thành 08:00. Job riêng `CareFollowUpSweepJob` chạy **mỗi phút** (lịch hẹn tới giờ + gợi ý quá hạn); phân tích khách im lặng vẫn 15 phút/lần (tốn AI hơn, không cần chính xác tới phút).

## 2026-10-05 — Bot TỰ chăm sóc khách (đưa phần lõi của GĐ2 lên trước M6 đợt B)

Chủ dự án chốt: "AI phải tự nhắn chăm sóc khách như một nhân viên, chỉ khi cần người mới gọi người vào" — đây là lõi sản phẩm. Mặc định DN mới: **bot tự nhắn** (tắt được: "Chỉ gợi ý").
- **Luồng:** khách im lặng ≥ X giờ (quét 15 phút) hoặc tới giờ hẹn trong nhật ký (quét mỗi phút) → AI chọn `send` / `ask_human` / `none` + tin nhắn viết bằng giọng TRỢ LÝ AI (không giả làm nhân viên) → `CareDecision` (luật cứng) quyết định → bot gửi qua đúng đường gửi tin (`ProactiveSendJob`, tin đánh dấu `messages.proactive`), hoặc gợi ý + Telegram cho nhân viên. Khách trả lời thì bot nói chuyện tiếp như thường; cần người thì chuyển như M5.
- **Giới hạn cứng (`CareDecision`):** mỗi lần khách im lặng chỉ 1 tin chủ động — chưa được trả lời thì không nhắn tin thứ 2 (`contacts.proactive_awaiting_reply`; riêng lịch hẹn nhân viên đặt vẫn gửi); cách tin chủ động trước ≥ 24 giờ; chỉ trong khung giờ DN chọn, luôn bị kẹp trong 07:00–21:00 giờ VN (ngoài giờ → hẹn Hangfire tới giờ mở); quá 7 ngày Zalo → nhân viên gọi điện; khách nhắn đúng một lệnh "hủy"/"dừng nhắn tin"/"stop"... → `proactive_opt_out` (nguồn customer, nhân viên không bật lại được), bot xác nhận cố định, không gọi AI. Không coi "dừng"/"dung" đứng một mình là lệnh (bỏ dấu trùng "đúng").
- **Luôn chuyển nhân viên (không tự gửi):** AI đánh giá phàn nàn / sức khỏe-nhạy cảm / cần thông tin chỉ nhân viên có; tin có câu cấm hoặc **có giá tiền** (bot tự nhắn không được nêu giá); **tin quảng cáo** (ưu đãi, kéo khách cũ) vì chưa có dữ liệu "đồng ý nhận tin" — làm cùng GĐ2 (consent, ZNS); nhân viên đang phụ trách hội thoại (mode human).
- **Kiểm tra lại lúc gửi:** khách vừa nhắn, vừa hủy, nhân viên vừa tiếp quản, DN vừa tắt tự nhắn → không gửi. Chạy lại an toàn (gợi ý đã gửi chỉ gửi nốt tin pending). Job quét mỗi phút đẩy lại tin hẹn giờ bị trễ > 5 phút.
- **Cần luật sư xác nhận** ranh giới "chăm sóc" (được tự nhắn) và "quảng cáo" (cần đồng ý) trước khi bán thật.
