# CLAUDE.md — Trợ lý Zalo AI

> File này là "bộ não" của dự án cho Claude Code. Đặt ở thư mục gốc repo **backend** (`zalo-ai-assistant`, .NET).
> Dự án gồm 2 repo: repo này (BE .NET) và repo **frontend** riêng (Next.js, admin). File này là nguồn chính cho toàn dự án; repo FE có CLAUDE.md ngắn trỏ về đây.
> Claude đọc file này đầu mỗi phiên, làm theo **Quy trình làm việc** (mục 9) và **Kế hoạch từng module** (mục 11).
> Tiến độ được cập nhật ở `docs/PROGRESS.md` (mục 12).

---

## 1. Dự án là gì

Trợ lý AI chăm sóc khách hàng trên **Zalo Official Account (OA)**, cho doanh nghiệp **thuê theo tháng** (SaaS, multi-tenant).

- Khách cuối nhắn tin vào Zalo OA của doanh nghiệp → bot trả lời 24/7 **dựa trên dữ liệu riêng** doanh nghiệp nạp vào (RAG).
- Bot không chỉ trả lời mà còn **chủ động chăm sóc** qua flow: chào khách mới, nhắc lịch, hỏi thăm sau mua, xin đánh giá, kéo khách cũ.
- Chủ doanh nghiệp và nhân viên dùng **trang quản trị web** để nạp dữ liệu, xem hội thoại, tiếp quản chat, xem khách tiềm năng và báo cáo.
- Đa ngành: spa, nha khoa, bất động sản, sửa chữa nhà, shop bán lẻ, giáo dục... qua **mẫu ngành**.

Đội ngũ: 1 người (chủ dự án) + Claude. Chủ dự án đã từng code và deploy web app, chưa từng vận hành sản phẩm có người dùng thật → Claude phải giải thích các phần vận hành, bảo mật khi làm.

---

## 2. Nguyên tắc sản phẩm (không được vi phạm)

1. **Doanh nghiệp giữ OA của họ.** Chỉ kết nối qua OAuth cấp quyền cho Zalo App của mình. Không bao giờ yêu cầu mật khẩu Zalo.
2. **Chỉ dùng Zalo OA + API chính thức.** Không tự động hóa Zalo cá nhân, không scrape, không thư viện không chính thức.
3. **Bot chỉ trả lời theo dữ liệu được nạp.** Không chắc → nói thật và chuyển nhân viên. Không bịa giá, không bịa chính sách.
4. **Tách dữ liệu tuyệt đối giữa các khách thuê.** Lộ dữ liệu khách A sang khách B là lỗi nghiêm trọng nhất có thể xảy ra.
5. **Tuân thủ pháp luật Việt Nam bằng thiết kế** (mục 7): báo là trợ lý AI, che thông tin cá nhân trước khi gửi AI, đồng ý nhận tin, khung giờ gửi, xóa/xuất dữ liệu.
6. **Không mất tin nhắn.** Mọi tin vào đều qua hàng đợi, có retry, có log.

---

## 3. Kiến trúc

```
Khách cuối ──nhắn tin──▶ Zalo OA (của DN) ──webhook──▶ API (ASP.NET Core)
                                                         │ verify chữ ký, dedupe, trả 200 ngay
                                                         ▼
                                              Hangfire (lưu job trong Postgres)
                                                         │
                                                         ▼
                                     Worker ──tra cứu──▶ Postgres + pgvector (lọc tenant_id)
                                        │   ──gọi AI──▶ IAiProvider (Gemini dev / Claude prod)
                                        ▼
                         Zalo OA API (gửi trả lời bằng token của đúng DN)

Chủ DN, nhân viên ──trình duyệt──▶ Admin (Next.js, repo FE) ──HTTP──▶ API ──▶ Postgres
```

Redis vẫn dùng cho: khóa phân tán khi refresh token, OAuth `state` có TTL, dedupe webhook nhanh, rate limit gửi theo OA.

Luồng một tin nhắn:
1. Zalo gọi webhook kèm OA ID. API xác thực chữ ký, bỏ qua tin trùng (theo message id), đẩy job vào hàng đợi, trả 200 ngay.
2. Worker xác định `tenant_id` từ OA ID → kiểm tra hội thoại đang ở chế độ bot hay nhân viên.
3. Nếu bot: che PII → tìm đoạn tài liệu liên quan (chỉ trong kho của tenant) → build prompt → gọi AI → nhận JSON có cấu trúc.
4. Gửi trả lời qua Zalo API bằng access token của OA đó. Lưu tin, lưu chunk đã dùng, lưu chi phí.
5. Nếu AI báo cần người → chuyển hội thoại sang chế độ nhân viên + thông báo.

Kết nối OA:
- DN bấm "Kết nối Zalo OA" → redirect sang trang cấp quyền của Zalo (kèm `state` gắn với tenant, PKCE nếu docs yêu cầu) → Zalo gọi callback với `code` → đổi lấy access token + refresh token → mã hóa, lưu vào `channel_connections`.
- Job định kỳ làm mới token trước khi hết hạn. Refresh token có thể chỉ dùng được một lần → **bắt buộc khóa phân tán** (Redis lock) khi refresh.

---

## 4. Tech stack (chốt, không tự đổi)

| Thành phần | Chọn | Ghi chú |
| --- | --- | --- |
**Backend (repo này):**

| Thành phần | Chọn | Ghi chú |
| --- | --- | --- |
| Ngôn ngữ | C#, .NET 10 (LTS) | `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` |
| Solution | 1 file `.sln`, nhiều project trong `src/` + `tests/` | `Directory.Build.props` + `Directory.Packages.props` quản lý version tập trung |
| API | ASP.NET Core (Minimal API) | Xuất OpenAPI (`Microsoft.AspNetCore.OpenApi`) để FE sinh client |
| Validate | FluentValidation | |
| Worker / hàng đợi | Hangfire + `Hangfire.PostgreSql` | API enqueue, process Worker chạy Hangfire server, scale được nhiều bản. Có dashboard (chỉ super admin) |
| Database | Postgres 16 + pgvector | |
| ORM | EF Core + Npgsql + `Pgvector.EntityFrameworkCore` + `EFCore.NamingConventions` (snake_case) | Migration bằng `dotnet ef` |
| Redis | StackExchange.Redis | Lock, OAuth state, dedupe, rate limit gửi |
| Auth | Email + mật khẩu (`PasswordHasher` của ASP.NET Core Identity), cookie authentication httpOnly | |
| Rate limit | Middleware rate limiting có sẵn của ASP.NET Core | |
| Gọi HTTP ra ngoài | `HttpClientFactory` + `Microsoft.Extensions.Http.Resilience` (Polly) | Timeout, retry, circuit breaker |
| AI chat | `IAiProvider`: Gemini (dev, free) và Anthropic Claude (prod) | Model đặt trong config, không hardcode. SDK chính thức nếu ổn định, không thì gọi REST qua HttpClient (chốt ở M3) |
| Embedding | Qua `IAiProvider.EmbedAsync` (Gemini embedding lúc đầu) | Số chiều vector đặt trong config. Đổi model = tạo lại toàn bộ vector |
| Parse file | PdfPig (pdf), DocumentFormat.OpenXml (docx), ClosedXML (xlsx) | Chạy trong worker |
| Logging | Serilog (JSON) | Không log token, không log PII; có destructuring policy để redact |
| Lỗi | Sentry (`Sentry.AspNetCore`) | |
| Test | xUnit + Testcontainers for .NET (Postgres, Redis) | Assert bằng xUnit/Shouldly (không dùng FluentAssertions v8: license thương mại) |
| Format/lint | `dotnet format` + .NET analyzers | |

**Frontend (repo riêng):**

| Thành phần | Chọn | Ghi chú |
| --- | --- | --- |
| Admin | Next.js (App Router) + TypeScript strict + Tailwind + shadcn/ui | |
| Gọi API | `openapi-typescript` + `openapi-fetch`, sinh type từ OpenAPI của BE | Không viết tay type API |
| Cookie | Next.js rewrite `/api/*` → BE, để cookie session cùng domain | |

**Chung:**

| Thành phần | Chọn | Ghi chú |
| --- | --- | --- |
| Local dev | Docker Compose (postgres pgvector, redis) + ngrok/cloudflared cho webhook | Nằm trong repo BE |
| Deploy | Docker. BE (api + worker): Railway hoặc VPS. FE: Vercel (gói Pro khi thương mại) hoặc Railway | Staging và production tách riêng; FE và API chung tên miền gốc |

Thêm thư viện ngoài danh sách → **hỏi chủ dự án trước**.

---

## 5. Cấu trúc thư mục

Hai repo nằm cạnh nhau:
```
C:\Zalo_Tool\
├── zalo-ai-assistant\     # repo BE (.NET) — repo này
└── zalo-ai-admin\         # repo FE (Next.js)
```

**Repo BE (`zalo-ai-assistant`):**
```
/
├── CLAUDE.md
├── docs/
│   ├── PROGRESS.md            # tiến độ, cập nhật sau mỗi task
│   ├── DECISIONS.md           # ghi lại quyết định kỹ thuật quan trọng
│   └── zalo-api-notes.md      # ghi chú API Zalo đã kiểm chứng từ docs chính thức
├── .claude/skills/            # skills (mục 10) tách ra từ file này ở M1
├── ZaloAi.sln
├── Directory.Build.props      # cài đặt chung: net10.0, nullable, warnings as errors
├── Directory.Packages.props   # version NuGet tập trung
├── src/
│   ├── ZaloAi.Api/            # ASP.NET Core: REST cho admin, webhook, OAuth callback, enqueue job
│   ├── ZaloAi.Worker/         # Hangfire server: message, ingest, token-refresh, flow
│   ├── ZaloAi.Core/           # entity, interface, options (config), lỗi chung. Không phụ thuộc hạ tầng
│   ├── ZaloAi.Infrastructure/ # EF Core DbContext, migrations, repository có tenant, crypto, Redis, Serilog redact
│   ├── ZaloAi.Ai/             # IAiProvider, prompt builder, PII masking, RAG search, evals/
│   ├── ZaloAi.Channels/       # IChannelAdapter + Zalo adapter + webchat adapter
│   └── ZaloAi.IndustryTemplates/ # mẫu ngành (file nhúng): prompt, câu cấm, câu hỏi thu thập, flow mẫu
├── tests/
│   ├── ZaloAi.UnitTests/
│   └── ZaloAi.IntegrationTests/  # Testcontainers: test cô lập tenant, API, job
├── docker-compose.yml
└── .env.example
```

Phụ thuộc giữa project: `Api`, `Worker` → `Infrastructure`, `Ai`, `Channels` → `Core`. `Core` không tham chiếu project nào.
`Channels` dùng interface chung để sau này thêm Messenger, chat website mà không sửa worker.

**Repo FE (`zalo-ai-admin`):** Next.js App Router, `src/app/` (trang), `src/lib/api/` (client sinh từ OpenAPI của BE, lệnh `pnpm gen:api`), `src/components/`. FE **chỉ gọi BE qua HTTP**, không chứa secret, không kết nối DB.

---

## 6. Biến môi trường (`.env.example`)

BE dùng quy ước .NET: `__` (hai gạch dưới) phân cấp section, ví dụ `Ai__ChatModel` ↔ `Ai:ChatModel` trong `appsettings.json`. Secret chỉ đặt trong env hoặc `dotnet user-secrets` (dev), không đặt trong `appsettings.json`.

```
ASPNETCORE_ENVIRONMENT=Development
App__AdminUrl=http://localhost:3000          # URL FE (CORS, redirect sau OAuth)
App__ApiUrl=http://localhost:4000
App__PublicWebhookBaseUrl=                   # URL https từ ngrok/cloudflared

ConnectionStrings__Postgres=Host=localhost;Port=5432;Database=zaloai;Username=app;Password=app
ConnectionStrings__Redis=localhost:6379

Security__EncryptionKey=                     # 32 bytes base64, dùng AES-256-GCM cho token và dữ liệu nhạy cảm

Ai__ChatProvider=gemini                      # gemini | anthropic
Ai__ChatModel=
Ai__EmbedProvider=gemini
Ai__EmbedModel=
Ai__EmbedDim=
Ai__GeminiApiKey=
Ai__AnthropicApiKey=

Zalo__AppId=
Zalo__AppSecret=
Zalo__OAuthRedirectUrl=

Sentry__Dsn=
Telegram__BotToken=                          # kênh thông báo nhân viên giai đoạn đầu
```

FE (`zalo-ai-admin/.env.local`): `API_INTERNAL_URL=http://localhost:4000` (đích rewrite `/api/*`). Không có secret nào ở FE.

Không commit `.env`. Mọi config đọc qua Options pattern (`IOptions<T>` trong `ZaloAi.Core`), validate bằng `ValidateDataAnnotations().ValidateOnStart()`, thiếu biến bắt buộc thì crash sớm khi khởi động.

---

## 7. Pháp lý và tuân thủ bằng thiết kế

Căn cứ (chủ dự án cần luật sư xác nhận, kiểm tra văn bản hiện hành): Nghị định 13/2023 và Luật Bảo vệ dữ liệu cá nhân (hiệu lực 2026), chống tin nhắn rác (Nghị định 91/2020 và sửa đổi), Luật Quảng cáo, Luật Bảo vệ quyền lợi người tiêu dùng 2023, quy định về AI, chính sách Zalo.

Tính năng bắt buộc trong code:

| Tính năng | Module |
| --- | --- |
| Tin chào đầu tiên báo đây là trợ lý AI + link chính sách bảo mật của DN (cấu hình được link, không tắt được câu báo AI) | M3 |
| Che số điện thoại, email, tên, địa chỉ, số giấy tờ trước khi gửi nội dung cho AI; lưu bản gốc ở DB đã mã hóa | M3 |
| Danh sách câu cấm theo ngành, kiểm tra câu trả lời trước khi gửi | M3 |
| Mã hóa token OA và trường nhạy cảm (AES-256-GCM), phân quyền, audit log ai xem/xuất dữ liệu | M1, M4 |
| Xóa/xuất dữ liệu của một khách cuối; xóa toàn bộ dữ liệu tenant khi ngừng thuê | M6 |
| Ghi nhận đồng ý nhận tin trước khi vào flow chăm sóc; nhắn "hủy" là dừng; lưu lịch sử đồng ý/từ chối | GĐ2 |
| Khung giờ gửi và tần suất tối đa cài cứng ở tầng hệ thống | GĐ2 |

---

## 8. Quy tắc code bắt buộc

### Multi-tenant
- Mọi bảng dữ liệu của khách đều có `tenant_id NOT NULL`, có index.
- **Không bao giờ** truy vấn bảng có tenant mà không lọc `tenant_id`. Hai lớp bảo vệ:
  1. Repository trong `ZaloAi.Infrastructure/Repositories` nhận `Guid tenantId` là tham số đầu tiên; cấm dùng `DbContext` trực tiếp từ endpoint/job cho bảng có tenant.
  2. EF Core **global query filter** trên mọi entity `ITenantOwned`, lấy tenant từ `ITenantContext` (API: từ cookie session; Worker: từ job data). `SaveChanges` từ chối ghi entity có `TenantId` khác tenant hiện tại.
- Cấm `IgnoreQueryFilters()` và SQL thô không có `tenant_id`, trừ code super admin / job hệ thống đã ghi rõ lý do trong comment.
- Tìm kiếm vector luôn `WHERE tenant_id = $1` trước khi sắp xếp theo khoảng cách.
- Mỗi module có **test cô lập tenant**: tạo 2 tenant, chứng minh tenant A không đọc/ghi được dữ liệu B.

### Bảo mật
- Không log access token, refresh token, API key, số điện thoại, nội dung tin nhắn gốc. Logger có hàm redact.
- Webhook phải xác thực chữ ký theo docs Zalo. Sai chữ ký → 401, không xử lý.
- OAuth `state` ngẫu nhiên, lưu Redis có TTL, gắn tenant và user.
- Validate mọi input bằng FluentValidation (BE). Giới hạn dung lượng upload (mặc định 20MB/file).
- Rate limit các endpoint đăng nhập và webhook.

### Độ tin cậy
- Webhook: xác thực → dedupe → enqueue → trả 200. Không gọi AI trong request webhook.
- Job có retry với exponential backoff; job lỗi hết lượt vào dead-letter, có cảnh báo.
- Mọi lệnh gọi ra ngoài (Zalo, AI) có timeout.
- Idempotent: xử lý lại cùng một job không gửi trùng tin cho khách.

### Chung
- BE: nullable reference types bật, warnings as errors, `async` xuyên suốt và truyền `CancellationToken`. FE: TypeScript strict, không `any` trừ khi có comment giải thích.
- Đổi API (BE) → cập nhật OpenAPI → chạy `pnpm gen:api` ở repo FE, commit ở cả hai repo.
- Sửa schema = tạo migration mới, không sửa migration cũ.
- Commit nhỏ, message dạng `feat(m2): ...`, `fix(m4): ...`.
- **API Zalo:** không đoán endpoint, tham số, cơ chế chữ ký, thời hạn token. Kiểm tra docs chính thức (developers.zalo.me) hoặc hỏi chủ dự án, rồi ghi vào `docs/zalo-api-notes.md` kèm ngày kiểm tra.

---

## 9. Quy trình làm việc (workflow cho Claude Code)

Mỗi task làm theo đúng vòng này:

1. **Đọc** CLAUDE.md, `docs/PROGRESS.md`, và code liên quan.
2. **Lập kế hoạch**: liệt kê file sẽ tạo/sửa, schema thay đổi, thư viện cần thêm, rủi ro. **Dừng lại chờ chủ dự án đồng ý** trước khi code nếu task đụng schema, bảo mật, hoặc thêm thư viện.
3. **Code** theo từng bước nhỏ, mỗi bước chạy được.
4. **Test**: viết test cho logic chính + test cô lập tenant nếu đụng dữ liệu. BE: `dotnet format --verify-no-changes && dotnet build && dotnet test`. FE: `pnpm lint && pnpm typecheck && pnpm test`.
5. **Tự review** theo checklist:
   - [ ] Mọi truy vấn có `tenant_id`?
   - [ ] Không log token/PII?
   - [ ] Input đã validate?
   - [ ] Lỗi ngoài (Zalo, AI) có timeout + retry?
   - [ ] Migration mới nếu đổi schema?
6. **Cập nhật** `docs/PROGRESS.md` (tick task, ghi vấn đề còn mở) và `docs/DECISIONS.md` nếu có quyết định mới.
7. **Báo cáo** ngắn cho chủ dự án: làm gì, cách chạy thử, giải thích phần quan trọng (bảo mật, vận hành) bằng tiếng Việt dễ hiểu, việc chủ dự án cần tự làm (tạo key, cấu hình Zalo...).
8. Commit.

Khi không chắc về yêu cầu sản phẩm → hỏi, không tự đoán. Khi chủ dự án yêu cầu điều vi phạm mục 2 hoặc mục 8 → nói rõ rủi ro trước.

Lệnh thường dùng (tạo ở M1):
```
# repo BE (zalo-ai-assistant)
docker compose up -d
dotnet run --project src/ZaloAi.Api        # API, cổng 4000
dotnet run --project src/ZaloAi.Worker     # Hangfire worker
dotnet ef migrations add <Ten> -p src/ZaloAi.Infrastructure -s src/ZaloAi.Api
dotnet ef database update -p src/ZaloAi.Infrastructure -s src/ZaloAi.Api
dotnet run --project src/ZaloAi.Api -- seed   # 2 tenant mẫu + dữ liệu mẫu
dotnet format | dotnet build | dotnet test

# repo FE (zalo-ai-admin)
pnpm dev            # Next.js, cổng 3000
pnpm gen:api        # sinh type từ OpenAPI của BE
pnpm lint | pnpm typecheck | pnpm test
```

---

## 10. Skills

Các quy trình lặp lại. Ở M1, Claude tách mỗi skill dưới đây thành `.claude/skills/<name>/SKILL.md` (giữ nguyên frontmatter). Trước khi tách, chúng vẫn có hiệu lực từ file này.

### 10.1 `tenant-safe-feature`
```markdown
---
name: tenant-safe-feature
description: Quy trình thêm bảng, endpoint, job hoặc màn hình admin đụng tới dữ liệu khách thuê. Dùng mỗi khi thêm hoặc sửa bất kỳ tính năng nào đọc/ghi dữ liệu của doanh nghiệp, kể cả khi task không nhắc tới "tenant".
---
1. Bảng mới: thêm `tenant_id uuid not null references tenants(id) on delete cascade` + index.
2. Entity implement `ITenantOwned` (để global query filter tự áp dụng). Thêm hàm trong repository `src/ZaloAi.Infrastructure/Repositories/<Name>Repository.cs`, tham số đầu tiên luôn là `Guid tenantId`.
3. Endpoint API lấy `tenantId` từ `ITenantContext` (đọc từ cookie session), KHÔNG lấy từ body/query/route.
4. Job Hangfire nhận `tenantId` làm tham số và set vào `ITenantContext` đầu job; tenantId do API/webhook xác định, không suy ra từ input của khách cuối.
5. Kiểm tra quyền: owner, staff, super_admin.
6. Viết test cô lập (`tests/ZaloAi.IntegrationTests`, Testcontainers): tenant A tạo dữ liệu, tenant B gọi cùng API → 404 (không phải 403, để không lộ sự tồn tại).
7. Nếu có dữ liệu cá nhân: thêm vào luồng xóa/xuất dữ liệu (M6).
```

### 10.2 `db-migration`
```markdown
---
name: db-migration
description: Cách thay đổi schema Postgres/EF Core an toàn. Dùng mỗi khi thêm/sửa/xóa bảng, cột, index, hoặc đổi số chiều vector.
---
1. Sửa entity + cấu hình (`IEntityTypeConfiguration`) trong `src/ZaloAi.Infrastructure/Persistence/`.
2. `dotnet ef migrations add <Ten> -p src/ZaloAi.Infrastructure -s src/ZaloAi.Api`, rồi `dotnet ef migrations script` để đọc lại SQL sinh ra.
3. Không xóa cột đang dùng trong cùng một lần deploy: thêm cột mới → deploy code dùng cả hai → migrate dữ liệu → xóa cột cũ ở lần sau.
4. Index trên bảng lớn dùng `CREATE INDEX CONCURRENTLY` (migration riêng, `migrationBuilder.Sql(..., suppressTransaction: true)`).
5. Đổi model hoặc số chiều embedding: tạo cột/bảng vector mới, job re-embed toàn bộ, chuyển truy vấn, rồi mới xóa cái cũ.
6. Cập nhật seed nếu cần. Ghi vào DECISIONS.md nếu thay đổi lớn.
```

### 10.3 `add-industry-template`
```markdown
---
name: add-industry-template
description: Thêm mẫu ngành mới (spa, nha khoa, bất động sản, sửa chữa nhà...). Dùng khi chủ dự án muốn bot hỗ trợ ngành mới hoặc chỉnh kịch bản của ngành có sẵn.
---
Tạo `src/ZaloAi.IndustryTemplates/Templates/<slug>/` (file nhúng `EmbeddedResource`) gồm:
- `persona.md`: vai trò, giọng văn, xưng hô mặc định.
- `rules.md`: điều bot phải làm/không làm trong ngành.
- `forbidden.json`: cụm từ cấm (ví dụ ngành y, thẩm mỹ: "cam kết", "trị dứt điểm", "100%", "không tác dụng phụ"), kèm câu thay thế.
- `lead_fields.json`: thông tin cần thu thập (ví dụ sửa nhà: địa chỉ công trình, hạng mục, diện tích, thời gian muốn làm).
- `faq_sample.md`: dữ liệu mẫu để demo.
- `flows.json` (GĐ2): flow chăm sóc mẫu.
Đăng ký slug trong `IndustryTemplateRegistry.cs`. Viết test: prompt builder nạp đúng template; câu trả lời chứa cụm cấm bị chặn.
```

### 10.4 `add-ai-provider`
```markdown
---
name: add-ai-provider
description: Thêm hoặc đổi nhà cung cấp AI (Gemini, Claude, model khác). Dùng khi đổi model chat/embedding hoặc thêm provider mới.
---
1. Implement `IAiProvider` trong `src/ZaloAi.Ai/Providers/`:
   `Task<ChatResult> ChatAsync(ChatRequest req, CancellationToken ct)` — req: `System, Messages, JsonSchema?, MaxTokens, Timeout`; result: `Text, Json?, Usage(InputTokens, OutputTokens), CostUsd`
   `Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)`
2. Đọc model và key từ config (Options), không hardcode.
3. Map lỗi của provider về exception chung: `AiRateLimitedException`, `AiTimeoutException`, `AiProviderException` để job Hangfire retry đúng.
4. Tính chi phí từ bảng giá trong config (cập nhật thủ công theo trang giá của provider).
5. Chạy bộ eval nhỏ (`src/ZaloAi.Ai/Evals/`) so sánh chất lượng trả lời tiếng Việt với provider cũ trước khi đổi ở production.
6. Nhắc chủ dự án: production chỉ dùng gói trả phí có cam kết không dùng dữ liệu để huấn luyện.
```

### 10.5 `debug-bot-answer`
```markdown
---
name: debug-bot-answer
description: Điều tra vì sao bot trả lời sai, bịa, hoặc không trả lời. Dùng khi chủ dự án hoặc khách báo "bot nói sai giá", "bot trả lời lạ", "bot không trả lời".
---
1. Tìm tin nhắn trong `messages` (theo tenant, thời gian). Lấy `ai_trace` gồm: chunk ids đã dùng, prompt version, model, output JSON, lý do handoff.
2. Kiểm tra theo thứ tự:
   a. Dữ liệu có thông tin đúng không? (tài liệu cũ, chưa ingest xong, ingest lỗi)
   b. Search có lấy đúng chunk không? (chạy lại search với câu hỏi, xem điểm similarity)
   c. Prompt có hướng dẫn đúng không?
   d. Model có làm theo không?
3. Sửa đúng tầng gây lỗi. Thêm câu hỏi đó vào bộ eval để không tái diễn.
4. Không tắt quy tắc "không biết thì chuyển người" để sửa lỗi.
```

### 10.6 `zalo-api-work`
```markdown
---
name: zalo-api-work
description: Làm bất cứ việc gì với API Zalo (OAuth, webhook, gửi tin, ZNS, token). Dùng mỗi khi code đụng tới src/ZaloAi.Channels/Zalo hoặc callback/webhook Zalo.
---
1. Đọc `docs/zalo-api-notes.md` trước. Nếu thông tin cần dùng chưa có hoặc cũ hơn 3 tháng → nhờ chủ dự án kiểm tra docs chính thức, ghi lại kèm ngày.
2. Mọi lệnh gọi Zalo đi qua `ZaloClient` (typed HttpClient) trong adapter, có timeout, retry (resilience handler), log (đã redact).
3. Refresh token: dùng Redis lock theo OA, đọc lại token sau khi lấy được lock (có thể worker khác đã refresh), lưu token mới trong cùng transaction.
4. Lỗi token không hợp lệ/bị thu hồi → đánh dấu connection `needs_reauth`, thông báo DN, dừng gửi.
5. Tôn trọng khung thời gian được phép nhắn chủ động; ngoài khung chỉ gửi ZNS theo mẫu đã duyệt.
6. Test bằng fixture payload thật (đã xóa PII) lưu ở `tests/ZaloAi.UnitTests/Fixtures/Zalo/`.
```

### 10.7 `add-care-flow` (GĐ2)
```markdown
---
name: add-care-flow
description: Thêm loại flow chăm sóc khách hàng hoặc bước mới trong flow (chờ, điều kiện, gửi tin, gắn nhãn, báo nhân viên). Dùng khi làm tính năng chăm sóc chủ động.
---
1. Flow = trigger → [condition] → action → wait → ... lưu dạng JSON (jsonb), validate bằng FluentValidation.
2. Trước mỗi lần gửi: kiểm tra đồng ý nhận tin, khung giờ, tần suất tối đa, điều kiện dừng (đã mua, đã phản hồi, đã hủy).
3. Chọn kênh: còn trong khung → tin tư vấn; ngoài khung → ZNS (nếu có mẫu phù hợp) → không gửi được thì bỏ qua và ghi log.
4. Khách trả lời giữa flow → chuyển về luồng hội thoại bình thường, flow tạm dừng.
5. Mọi lần gửi ghi vào `flow_runs` để báo cáo và làm bằng chứng tuân thủ.
```

---

## 11. Kế hoạch từng module

Thứ tự bắt buộc: M0 → M1 → M2 → M3 → M4 → M5 → M6. Không nhảy module khi module trước chưa đạt "Tiêu chí xong".
Ước lượng tính theo làm toàn thời gian; làm ngoài giờ thì nhân đôi.

### M0 — Việc chủ dự án tự làm (tuần 0, song song, không cần code)
- [ ] Đăng ký Zalo OA test (oa.zalo.me), xác thực theo yêu cầu của Zalo.
- [ ] Tạo Zalo App (developers.zalo.me), bật Official Account API, lấy App ID + Secret. Nộp xét duyệt sớm.
- [ ] Mua tên miền. Tạo tài khoản riêng cho dự án: GitHub, cloud, Sentry.
- [ ] Lấy Gemini API key (free) cho dev.
- [ ] Chọn 1–2 ngành đầu, liên hệ 2–3 doanh nghiệp dùng thử, xin dữ liệu thật (bảng giá, FAQ, quy trình).

### M1 — Nền tảng (tuần 1)
Task:
- [ ] BE: solution .NET 10 (`ZaloAi.sln`, `Directory.Build.props`, `Directory.Packages.props`), analyzers, `dotnet format`, xUnit; docker-compose (pgvector/pgvector:pg16, redis:7).
- [ ] `ZaloAi.Core` + `ZaloAi.Infrastructure`: Options có validate, Serilog có redact, crypto AES-256-GCM, exception chung, `ITenantContext`.
- [ ] EF Core: DbContext + migration đầu tiên (bảng bên dưới), global query filter tenant, repository có tenant, seed 2 tenant.
- [ ] `ZaloAi.Api`: `/health`, auth (đăng nhập, đăng xuất, cookie httpOnly), gắn `tenantId` + `role`, rate limit, OpenAPI, endpoint cài đặt tenant.
- [ ] `ZaloAi.Worker`: Hangfire server, 1 job mẫu, retry backoff, job lỗi hết lượt → cảnh báo.
- [ ] Repo FE `zalo-ai-admin`: Next.js, trang đăng nhập, layout, trang cài đặt tenant (tên, ngành, tên bot, xưng hô, link chính sách bảo mật); rewrite `/api/*` → BE; `pnpm gen:api`.
- [ ] Sentry cho api + worker. Script backup database.
- [ ] Tách skills ra `.claude/skills/`. Tạo `docs/PROGRESS.md`, `docs/DECISIONS.md`, `docs/zalo-api-notes.md`.

Bảng dữ liệu khởi đầu (thêm dần theo module):
```
tenants(id, name, industry_slug, bot_name, bot_pronoun, privacy_url, plan, status, expires_at, created_at)
users(id, email, password_hash, name, created_at)
memberships(user_id, tenant_id, role: owner|staff)          -- super_admin là cờ trên users
channel_connections(id, tenant_id, channel: zalo, external_id /*oa_id*/, name,
                    access_token_enc, refresh_token_enc, expires_at, status: active|needs_reauth|revoked)
contacts(id, tenant_id, channel, external_user_id, display_name, phone_enc, fields jsonb,
         tags text[], lead_status, consent_marketing, consent_at, created_at)
conversations(id, tenant_id, contact_id, connection_id, mode: bot|human, assigned_user_id,
              last_customer_msg_at, summary, status)
messages(id, tenant_id, conversation_id, direction: in|out, sender: customer|bot|staff,
         content_enc, external_msg_id unique, ai_trace jsonb, created_at)
documents(id, tenant_id, title, source_type: file|faq|url, status: pending|processing|ready|failed, error, created_at)
chunks(id, tenant_id, document_id, content, embedding vector(AI_EMBED_DIM), meta jsonb)
usage_records(id, tenant_id, kind: chat|embed|zns, provider, model, input_tokens, output_tokens, cost_usd, created_at)
audit_logs(id, tenant_id, user_id, action, target, created_at)
```

Tiêu chí xong: `docker compose up` + `dotnet run` (api, worker) + `pnpm dev` (FE) chạy được; đăng nhập admin; test cô lập tenant cho repository đầu tiên pass; CI ở cả 2 repo chạy format/build/test.

### M2 — Kho kiến thức (tuần 2)
Task:
- [ ] API upload file (pdf, docx, xlsx, txt, md) ≤ 20MB → lưu file (local dev / object storage prod) → tạo `documents` pending → enqueue `ingest`.
- [ ] Worker ingest: parse → làm sạch → chia chunk (~500–800 token, overlap ~15%, giữ tiêu đề mục trong meta) → embed theo batch → lưu `chunks`. Cập nhật status, lỗi thì ghi `error`.
- [ ] CRUD FAQ (câu hỏi + trả lời) → mỗi FAQ là một document nhỏ, sửa là re-embed.
- [ ] Xóa document → xóa chunks.
- [ ] Index HNSW trên `embedding`; hàm `searchChunks(tenantId, query, k)` lọc tenant trước.
- [ ] Admin: trang Kho kiến thức (danh sách, trạng thái, upload, FAQ, xóa), ô "thử tìm kiếm".

Tiêu chí xong: upload bảng giá thật của DN dùng thử, tìm "giá sơn lại phòng 20m2" (ví dụ) ra đúng chunk; tenant B không tìm thấy dữ liệu tenant A (có test).

### M3 — Lõi AI + chat thử (tuần 3)
Task:
- [ ] `aiProvider` với 2 implementation: gemini, anthropic (skill 10.4).
- [ ] PII masking: nhận diện SĐT VN, email, số CCCD, (tên/địa chỉ nếu khách tự khai trong form thu thập) → thay bằng placeholder trước khi gửi AI; ghép lại khi cần.
- [ ] Prompt builder: persona + rules ngành + cài đặt tenant + chunks (có id) + tóm tắt hội thoại + N tin gần nhất.
- [ ] Output JSON có cấu trúc:
  `{ reply, used_chunk_ids[], confidence: high|medium|low, needs_human, handoff_reason, lead_fields{}, sentiment }`
  Parse lỗi → retry 1 lần → vẫn lỗi thì trả câu mặc định và chuyển người.
- [ ] Quy tắc: không có chunk liên quan hoặc confidence low → không trả lời bừa, xin thông tin liên hệ và chuyển người.
- [ ] Bộ lọc câu cấm theo ngành chạy trên `reply` trước khi gửi.
- [ ] Tin chào đầu tiên có câu báo trợ lý AI + link chính sách.
- [ ] Tóm tắt hội thoại dài định kỳ để tiết kiệm token.
- [ ] Ghi `ai_trace` và `usage_records` cho mọi lần gọi.
- [ ] `ChannelAdapter` interface + adapter `webchat` (dùng cho khung chat thử trong admin, cùng luồng hàng đợi/worker với Zalo sau này).
- [ ] Admin: trang "Chat thử" và trang cài đặt giọng văn.
- [ ] `src/ZaloAi.Ai/Evals/`: 20–30 câu hỏi mẫu của ngành đầu tiên + đáp án mong đợi, script chạy eval.

Tiêu chí xong: chủ DN dùng thử tự chat trên khung chat thử và đánh giá câu trả lời đạt; eval pass ≥ mức chủ dự án chốt; câu hỏi ngoài dữ liệu được chuyển người, không bịa. **Chưa đạt thì chưa sang M4.**

### M4 — Kết nối Zalo (tuần 4)
Trước khi code: dùng skill `zalo-api-work`, cập nhật `docs/zalo-api-notes.md` (OAuth cho OA, đổi/làm mới token, thời hạn token, chữ ký webhook, API gửi tin tư vấn, các loại sự kiện, giới hạn tốc độ).
Task:
- [ ] Adapter `zalo`: `ZaloClient` (timeout, retry, redact), parse sự kiện webhook → định dạng chung.
- [ ] OAuth: `/connect/zalo/start` (tạo state, PKCE nếu cần) → `/connect/zalo/callback` → lưu connection mã hóa. Hỗ trợ nhiều OA/tenant.
- [ ] Webhook `/webhooks/zalo`: xác thực chữ ký → dedupe `external_msg_id` → tìm connection theo OA ID → enqueue `incoming-message` → 200.
- [ ] Worker dùng chung luồng với webchat; gửi trả lời qua Zalo API.
- [ ] Job `token-refresh` chạy định kỳ, refresh trước hạn, Redis lock theo OA; lỗi → `needs_reauth` + thông báo.
- [ ] Xử lý sự kiện: tin text, ảnh (GĐ1 chỉ ghi nhận + chuyển người hoặc trả lời chung), follow/unfollow.
- [ ] Rate limit gửi theo OA; retry với backoff.
- [ ] Admin: trang Kết nối kênh (kết nối, trạng thái, ngắt kết nối).
- [ ] Hướng dẫn chạy local với ngrok/cloudflared trong README.

Tiêu chí xong: nhắn vào OA test từ Zalo cá nhân → nhận câu trả lời đúng trong vài giây; ngắt kết nối → dừng trả lời; giả lập token hết hạn → tự refresh; gửi webhook trùng → chỉ trả lời 1 lần; chữ ký sai → 401.

### M5 — Inbox + tiếp quản (tuần 5)
Task:
- [ ] API danh sách hội thoại (lọc: cần xử lý, đang bot, đang người, theo nhãn), chi tiết tin nhắn.
- [ ] Realtime bằng SSE (đơn giản hơn websocket) cho admin.
- [ ] Nút "Tiếp quản" (mode=human, gán nhân viên) và "Trả lại cho bot".
- [ ] Nhân viên gửi tin từ admin → đi qua adapter kênh.
- [ ] Tự chuyển người khi: `needs_human`, khách đòi gặp người, sentiment tiêu cực, low confidence liên tiếp. Bot gửi câu chuyển tiếp lịch sự.
- [ ] Hội thoại ở mode human quá X phút không ai trả lời → nhắc lại / cảnh báo.
- [ ] Thông báo nhân viên qua Telegram (GĐ1), mỗi tenant cấu hình chat id.
- [ ] Phân quyền owner/staff cơ bản; audit log khi xem/xuất dữ liệu.

Tiêu chí xong: nhân viên thấy tin mới realtime, tiếp quản và trả lời từ admin tới được Zalo của khách; bot không chen vào khi đang human.

### M6 — Khách tiềm năng, báo cáo, quản trị (tuần 6)
Task:
- [ ] Gộp `lead_fields` từ AI vào `contacts` (theo template ngành), lead_status tự động + sửa tay.
- [ ] Trang Khách tiềm năng: lọc, xem hội thoại, xuất Excel (ghi audit log).
- [ ] Dashboard: hội thoại/ngày, khách tiềm năng mới, tỷ lệ bot tự xử lý, danh sách câu bot không trả lời được.
- [ ] Xóa/xuất dữ liệu một khách cuối; xóa toàn bộ dữ liệu tenant.
- [ ] Super admin (chỉ chủ dự án): danh sách tenant, gói, hạn, trạng thái, chi phí AI theo tháng, khóa/mở tenant.
- [ ] Hạn mức hội thoại/tháng theo gói: gần hết → cảnh báo; hết → theo cấu hình (chuyển hết sang người hoặc chặn).

Tiêu chí xong: DN dùng thử xem được lead và báo cáo tuần; chủ dự án thấy chi phí AI từng tenant.

### Tuần 7–8 — Dùng thử thật và chuẩn bị bán
- [ ] Deploy staging + production tách riêng; domain, HTTPS, backup, UptimeRobot, Sentry alert.
- [ ] Load test (k6 hoặc script): giả lập 50 hội thoại đồng thời, đo độ trễ và chi phí.
- [ ] DN dùng thử chạy thật; đọc log hằng ngày, thêm câu sai vào eval, sửa.
- [ ] Đo chi phí AI trung bình/hội thoại → chủ dự án chốt giá gói.
- [ ] Chủ dự án: đăng ký kinh doanh, luật sư làm điều khoản sử dụng, chính sách bảo mật, hợp đồng dịch vụ, hợp đồng xử lý dữ liệu, hồ sơ đánh giá tác động, hồ sơ chuyển dữ liệu ra nước ngoài.

### Giai đoạn 2 (sau khi có khách trả tiền, ~1–2 tháng)
- Flow chăm sóc khách hàng (skill `add-care-flow`): chào khách mới, khách chưa chốt, nhắc lịch, hỏi thăm sau mua, đánh giá hài lòng, chăm sóc định kỳ, sinh nhật, kéo khách cũ, xử lý phàn nàn. Đồng ý nhận tin, khung giờ, tần suất, ZNS.
- Mẫu ngành đầy đủ, chọn ngành khi đăng ký.
- Nhiều nhân viên, chia hội thoại; nhãn tự động; đồng bộ Google Sheets.
- Đặt lịch hẹn qua chat + nhắc lịch.
- Hiểu ảnh khách gửi; giờ làm việc; nạp dữ liệu từ link website.
- Báo cáo đầy đủ, báo cáo tuần tự động.

### Giai đoạn 3 (~1–2 tháng)
- Trình kéo thả tự thiết kế flow.
- Thanh toán tự động (QR chuyển khoản, cổng thanh toán), hóa đơn.
- Zalo Mini App (đặt lịch, xem sản phẩm), PWA cho admin.
- Kênh Facebook Messenger, chat website (adapter mới trong `src/ZaloAi.Channels`).

---

## 12. Mẫu `docs/PROGRESS.md`

```markdown
# Tiến độ

## Đang làm
- Module: M_
- Task: ...
- Chặn bởi: ...

## Đã xong
- [x] M1: ... (ngày)

## Vấn đề mở / nợ kỹ thuật
- ...

## Việc chủ dự án cần làm
- ...
```

---

## 13. Câu lệnh bắt đầu phiên cho chủ dự án

Dán vào Claude Code khi mở phiên mới:

> Đọc CLAUDE.md và docs/PROGRESS.md. Cho tôi biết đang ở module nào, task tiếp theo là gì, rồi lập kế hoạch chi tiết cho task đó theo mục 9. Chờ tôi đồng ý rồi mới code.

Phiên đầu tiên:

> Đọc CLAUDE.md. Bắt đầu M1. Lập kế hoạch dựng monorepo theo mục 4, 5, 6 và bảng dữ liệu ở M1. Chờ tôi đồng ý rồi mới code.
