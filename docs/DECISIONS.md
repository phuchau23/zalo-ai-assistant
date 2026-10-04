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
