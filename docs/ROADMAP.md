# Lộ trình đến phát hành — Trợ lý Zalo AI

> Lập ngày 2026-10-01. Bản chi tiết của mục 11 trong CLAUDE.md, đã đổi sang stack .NET (BE) + Next.js (FE `zalo-ai-portal`).
> Tiến độ thực tế ghi ở `docs/PROGRESS.md`. File này chỉ sửa khi kế hoạch thay đổi.
> Chi tiết tính năng: `docs/FEATURE-SPECS.md`. Mẫu ngành và quy tắc theo ngành: `docs/INDUSTRIES.md`.

---

## Tổng quan

| Phase | Tên | Thời gian (toàn thời gian) | Ngoài giờ | Kết quả |
| --- | --- | --- | --- | --- |
| 0 | Chuẩn bị | tuần 0 | 1–2 tuần | Máy dev chạy được, có OA test + Zalo App, có DN dùng thử |
| 1 | Nền tảng | tuần 1 | 2 tuần | Đăng nhập portal, DB có tenant, worker chạy job |
| 2 | Kho kiến thức | tuần 2 | 2 tuần | Upload bảng giá → tìm kiếm ra đúng đoạn |
| 3 | Lõi AI + chat thử | tuần 3 | 2–3 tuần | Chat thử trên portal trả lời đúng, không bịa — **cổng chất lượng** |
| 4 | Kết nối Zalo | tuần 4 | 2 tuần | Nhắn vào OA thật → bot trả lời |
| 5 | Inbox + tiếp quản | tuần 5 | 2 tuần | Nhân viên xem và trả lời realtime |
| 6 | Lead, báo cáo, quản trị | tuần 6 | 2 tuần | DN xem lead + báo cáo; bạn xem chi phí AI |
| 7 | Hạ tầng production | tuần 7 | 1–2 tuần | Staging + production chạy, có backup, giám sát |
| 8 | Dùng thử thật (pilot) | tuần 7–9 | 3–4 tuần | 2–3 DN dùng thật, số liệu chi phí/hội thoại |
| 9 | Phát hành v1 | tuần 10 | 1–2 tuần | Khách trả tiền đầu tiên |
| — | Song song: pháp lý + thương mại | từ tuần 0 | | Giấy tờ đủ trước Phase 9 |

**Tổng: ~10 tuần nếu làm toàn thời gian, ~5 tháng nếu làm ngoài giờ.**
Sau v1: Giai đoạn 2 (flow chăm sóc chủ động) và Giai đoạn 3 (xem cuối file).

Quy tắc chung cho mọi phase:
- Mỗi phase có **Tiêu chí xong**. Chưa đạt thì không sang phase sau.
- Mỗi bước code: Claude lập kế hoạch → bạn duyệt → code → test → báo cáo → commit. Dừng sau mỗi bước để bạn chạy thử.
- Ký hiệu: 🧑 = việc bạn tự làm, 🤖 = việc Claude code.

---

## Phase 0 — Chuẩn bị

**Mục tiêu:** đủ công cụ, tài khoản và đối tác để bắt đầu code và có dữ liệu thật để thử.

🧑 Máy dev:
- [x] Node 24 + pnpm
- [ ] .NET 10 SDK + `dotnet-ef`
- [ ] WSL + Docker Desktop + giới hạn RAM WSL (`.wslconfig`)

🧑 Zalo (**làm sớm nhất có thể**, xét duyệt của Zalo có thể mất nhiều ngày đến vài tuần):
- [ ] Đăng ký Zalo OA test (oa.zalo.me), xác thực theo yêu cầu.
- [ ] Tạo Zalo App (developers.zalo.me), bật Official Account API, lấy App ID + Secret.
- [ ] Đọc điều kiện xét duyệt app, nộp xét duyệt sớm.

🧑 Tài khoản dự án:
- [ ] Email riêng cho dự án; GitHub (2 repo: `zalo-ai-assistant`, `zalo-ai-portal`); Sentry; Gemini API key (free).
- [ ] Mua tên miền (chưa cần trỏ, Phase 7 mới dùng).

🧑 Khách hàng dùng thử:
- [ ] Chọn 1–2 ngành đầu tiên.
- [ ] Liên hệ 2–3 doanh nghiệp, xin dữ liệu thật: bảng giá, FAQ, quy trình, 20–30 câu khách hay hỏi.

🧑 Trả lời câu hỏi B1–B5 và duyệt kế hoạch Phase 1.

**Tiêu chí xong:** `dotnet --list-sdks` có 10.x; `docker run --rm hello-world` chạy; có ít nhất 1 DN đồng ý dùng thử và gửi dữ liệu.

---

## Phase 1 — Nền tảng (M1)

**Mục tiêu:** bộ khung chạy được end-to-end: portal đăng nhập → API → DB có tách tenant; worker chạy job.

| Bước | Repo | Việc 🤖 | Bạn chạy thử 🧑 |
| --- | --- | --- | --- |
| 1.1 | BE | Solution `ZaloAi.sln` (7 project + 2 test), cài đặt chung, `.editorconfig`, docker-compose (Postgres pgvector + Redis), `.env.example` | `docker compose up -d`, `dotnet build` |
| 1.2 | BE | Options có validate, Serilog có che token/PII, mã hóa AES-256-GCM, exception chung, `ITenantContext` | `dotnet test` |
| 1.3 | BE | EF Core: bảng khởi đầu, migration, global query filter tenant, repository, seed 2 tenant mẫu, **test cô lập tenant** | `dotnet ef database update`, mở DB xem dữ liệu |
| 1.4 | BE | API: `/health`, đăng nhập/đăng xuất (cookie httpOnly), phân quyền owner/staff/super_admin, rate limit, OpenAPI, API cài đặt tenant | Mở `/openapi`, gọi thử bằng trình duyệt |
| 1.5 | BE | Worker: Hangfire, job mẫu, retry, dashboard (chỉ super admin) | Xem job chạy trên dashboard |
| 1.6 | FE | Portal: đăng nhập, layout, trang cài đặt tenant (tên, ngành, tên bot, xưng hô, link chính sách bảo mật); `pnpm gen:api` | Đăng nhập bằng tài khoản seed, sửa cài đặt |
| 1.7 | cả 2 | Sentry, script backup DB, CI GitHub Actions | Push lên GitHub, xem CI xanh |
| 1.8 | BE | Tách skills ra `.claude/skills/`, tạo `docs/zalo-api-notes.md` | — |

**Tiêu chí xong:** chạy được cả 3 (API, worker, portal); đăng nhập và sửa cài đặt tenant; test cô lập tenant pass; CI xanh ở cả 2 repo.

**Bạn sẽ học được:** mã hóa token, tách tenant, cookie session, hàng đợi có retry — Claude giải thích khi làm.

---

## Phase 2 — Kho kiến thức (M2)

**Mục tiêu:** DN nạp tài liệu, hệ thống hiểu và tìm được đúng đoạn liên quan.

🤖 BE:
- Upload file (pdf, docx, xlsx, txt, md) ≤ 20MB → lưu file (dev: ổ đĩa; prod: object storage) → tạo `documents` → enqueue job ingest.
- Job ingest: đọc file → làm sạch → chia đoạn (~500–800 token, chồng lấn ~15%, giữ tiêu đề mục) → tạo embedding theo lô → lưu `chunks`. Lỗi thì ghi lý do.
- CRUD FAQ (mỗi câu hỏi + trả lời là một tài liệu nhỏ; sửa là tạo lại embedding).
- Xóa tài liệu → xóa các đoạn.
- Index HNSW; hàm `SearchChunks(tenantId, query, k)` lọc tenant trước rồi mới so khoảng cách.
- Test cô lập: tenant B không tìm thấy dữ liệu tenant A.

🤖 FE: trang **Kho kiến thức** (danh sách + trạng thái, upload, FAQ, xóa, ô "thử tìm kiếm").

🧑 Upload bảng giá thật của DN dùng thử, thử tìm 10–20 câu.

**Tiêu chí xong:** tìm "giá sơn lại phòng 20m2" (hoặc câu tương tự của ngành bạn chọn) ra đúng đoạn; test cô lập pass.

---

## Phase 3 — Lõi AI + chat thử (M3) ⛔ cổng chất lượng

**Mục tiêu:** bot trả lời đúng theo dữ liệu, không bịa, biết chuyển người. Đây là phase quyết định sản phẩm có bán được không.

🤖 BE:
- `IAiProvider` với 2 bản: Gemini (dev) và Claude (prod). Chốt dùng SDK chính thức hay gọi REST.
- Che PII: SĐT Việt Nam, email, CCCD (và tên/địa chỉ khi khách tự khai) → thay bằng mã giữ chỗ trước khi gửi AI.
- Prompt builder: vai trò + quy tắc ngành + cài đặt tenant + đoạn tài liệu (có id) + tóm tắt hội thoại + vài tin gần nhất.
- AI trả JSON: `reply, used_chunk_ids, confidence, needs_human, handoff_reason, lead_fields, sentiment`. JSON lỗi → thử lại 1 lần → vẫn lỗi thì gửi câu mặc định + chuyển người.
- Không có tài liệu liên quan hoặc độ tin cậy thấp → không trả lời bừa, xin thông tin liên hệ, chuyển người.
- Lọc câu cấm theo ngành trước khi gửi.
- Tin chào đầu tiên: báo là trợ lý AI + link chính sách bảo mật (không tắt được câu báo AI).
- Tóm tắt hội thoại dài; ghi `ai_trace` và `usage_records` (chi phí) cho mọi lần gọi.
- `IChannelAdapter` + adapter `webchat` (đi cùng hàng đợi/worker như Zalo sau này).
- Khung mẫu ngành theo `docs/INDUSTRIES.md` + mẫu ngành đầu tiên (persona, quy tắc, câu cấm, dấu hiệu nguy hiểm, trường lead, tài liệu bắt buộc).
- Gợi ý gói khi khách hỏi (từ danh mục DN nạp).
- Nếu ngành đầu là rủi ro cao (spa, nha khoa): **bộ an toàn y tế** — cờ `medically_reviewed` trên tài liệu, kiểm tra dấu hiệu nguy hiểm trước khi gọi AI + trường `urgency` trong output, chuyển người khẩn cấp (xem `INDUSTRIES.md` mục 3, `FEATURE-SPECS.md` mục 3).
- Bộ eval: 20–30 câu hỏi thật + đáp án mong đợi + câu bẫy; ngành rủi ro cao thêm ≥ 20 câu an toàn (phải đạt 100%). Lệnh chạy chấm điểm.

🤖 FE: trang **Chat thử**, trang **Giọng văn** (tên bot, xưng hô, phong cách).

🧑 Việc của bạn:
- Chốt **mức eval đạt** (ví dụ ≥ 85% đúng, 0 câu bịa giá).
- Mời chủ DN dùng thử tự chat trên portal và chấm.

**Tiêu chí xong:** eval đạt mức đã chốt; câu ngoài dữ liệu được chuyển người; chủ DN dùng thử đánh giá "dùng được". **Chưa đạt thì lặp lại Phase 3, không sang Phase 4.**

---

## Phase 4 — Kết nối Zalo (M4)

**Mục tiêu:** khách nhắn vào OA thật → bot trả lời trong vài giây.

🧑 Trước khi code: cung cấp/kiểm tra docs Zalo chính thức để Claude ghi vào `docs/zalo-api-notes.md` (OAuth cho OA, đổi/làm mới token, thời hạn token, chữ ký webhook, API gửi tin tư vấn, **thời gian OA được nhắn tư vấn sau tin cuối của khách**, loại sự kiện — **có sự kiện "OA gửi tin" khi nhân viên trả lời trong app Zalo không**, giới hạn tốc độ). Claude **không đoán** API Zalo.

🤖 BE:
- `ZaloClient` (timeout, retry, log đã che token).
- OAuth: bắt đầu kết nối (state ngẫu nhiên lưu Redis) → callback → lưu token đã mã hóa. Một tenant kết nối được nhiều OA.
- Webhook: xác thực chữ ký (sai → 401) → bỏ tin trùng → tìm tenant theo OA ID → enqueue → trả 200 ngay.
- Worker dùng chung luồng với webchat; gửi trả lời qua Zalo API.
- Job làm mới token định kỳ, có khóa Redis theo OA; lỗi → đánh dấu `needs_reauth` + báo DN.
- Xử lý: tin chữ, ảnh (ghi nhận + chuyển người), follow/unfollow.
- Giới hạn tốc độ gửi theo OA.

🤖 FE: trang **Kết nối kênh** (kết nối, trạng thái, ngắt kết nối). README hướng dẫn chạy webhook local bằng ngrok/cloudflared.

🧑 Dùng Zalo cá nhân nhắn vào OA test.

**Tiêu chí xong:** nhắn vào OA → nhận trả lời đúng trong vài giây; ngắt kết nối → bot dừng; giả lập token hết hạn → tự làm mới; gửi webhook trùng → chỉ trả lời 1 lần; chữ ký sai → 401.

---

## Phase 5 — Inbox + tiếp quản (M5)

**Mục tiêu:** nhân viên theo dõi và nhảy vào chat khi cần.

🤖 BE:
- API danh sách hội thoại (lọc: cần xử lý, bot đang trả lời, nhân viên đang trả lời, theo nhãn) + chi tiết tin nhắn.
- Realtime bằng SSE.
- "Tiếp quản" (chuyển sang nhân viên) và "Trả lại cho bot".
- Nhân viên gửi tin từ portal → đi qua adapter kênh.
- Tự chuyển người khi: AI báo cần người, khách đòi gặp người, khách bực bội, nhiều lần liên tiếp không chắc, dấu hiệu khẩn cấp.
- **Câu chuyển tiếp** cho 4 tình huống (bot chuyển người / nhân viên tiếp quản / nhân viên chủ động nhắn / trả lại bot), chữ ký nhân viên, giờ làm việc, cấu hình theo tenant — xem `FEATURE-SPECS.md` mục 1.
- Hội thoại chờ nhân viên quá X phút → nhắc lại.
- Thông báo Telegram (mỗi tenant cấu hình chat id).
- Phân quyền owner/staff; audit log khi xem/xuất dữ liệu.

🤖 FE: trang **Hội thoại** (danh sách realtime, khung chat, nút tiếp quản/trả lại); trang **Cài đặt → Chuyển tiếp** (sửa câu mẫu, chữ ký, giờ làm việc).

**Tiêu chí xong:** nhân viên thấy tin mới ngay, tiếp quản và trả lời từ portal tới được Zalo của khách; bot không chen vào khi nhân viên đang trả lời.

---

## Phase 6 — Lead, báo cáo, quản trị (M6)

**Mục tiêu:** DN thấy giá trị (lead, số liệu); bạn quản lý được các tenant và chi phí.

🤖 BE + FE:
- Gộp thông tin khách mà AI thu được vào `contacts`; trạng thái lead tự động + sửa tay.
- Trang **Khách tiềm năng**: lọc, xem hội thoại, xuất Excel (ghi audit log).
- Trang **Cần chăm sóc**: AI phân tích hội thoại "nguội" + quét khách cũ hằng ngày → danh sách khách nhân viên nên chủ động nhắn (mức độ, lý do, gợi ý, tin nháp, hạn nhắn OA, gán nhân viên), ghi kết quả để báo tỷ lệ chốt — xem `FEATURE-SPECS.md` mục 2. (+3–4 ngày)
- **Dashboard**: hội thoại/ngày, lead mới, tỷ lệ bot tự xử lý, danh sách câu bot không trả lời được.
- **Xóa/xuất dữ liệu** của một khách cuối; xóa toàn bộ dữ liệu tenant khi ngừng thuê (yêu cầu pháp lý).
- **Super admin** (chỉ bạn): danh sách tenant, gói, hạn, trạng thái, chi phí AI theo tháng, khóa/mở tenant, tạo tenant mới.
- Hạn mức hội thoại/tháng theo gói: gần hết → cảnh báo; hết → chuyển hết sang nhân viên hoặc chặn (theo cấu hình).

**Tiêu chí xong:** DN dùng thử xem được lead và báo cáo tuần; nhân viên nhắn được khách từ trang "Cần chăm sóc"; bạn thấy chi phí AI của từng tenant.

---

## Phase 7 — Hạ tầng production

**Mục tiêu:** hệ thống chạy thật ổn định, có backup, có cảnh báo khi lỗi.

🧑 Quyết định trước phase (Claude so sánh giúp):
- Nơi chạy BE: Railway hay VPS. Nơi chạy FE: Vercel (gói Pro khi thương mại) hay Railway.
- Postgres/Redis: dịch vụ quản lý sẵn hay tự chạy trên VPS.
- Object storage cho file upload.
- Gói AI production: Claude/Gemini trả phí, **có cam kết không dùng dữ liệu để huấn luyện**.

🤖 Việc:
- Dockerfile cho API, Worker, Portal.
- 2 môi trường tách riêng: **staging** (thử trước) và **production** (khách thật), mỗi bên DB, Redis, key riêng.
- Tên miền + HTTPS: `portal.<tenmien>` (FE), `api.<tenmien>` (BE).
- Migration DB tự động khi deploy (có bước kiểm tra), CI/CD: merge vào `main` → deploy staging; tag → deploy production.
- Backup DB hằng ngày + **thử khôi phục một lần** (backup chưa thử khôi phục coi như chưa có).
- Giám sát: Sentry cảnh báo lỗi, UptimeRobot kiểm tra `/health`, cảnh báo job lỗi hết lượt retry.
- Kiểm tra bảo mật: rà secret, header bảo mật, rate limit, phân quyền, chạy `/security-review`.
- Load test: giả lập 50 hội thoại đồng thời, đo độ trễ và chi phí.
- Tài liệu vận hành ngắn (`docs/RUNBOOK.md`): deploy, rollback, khôi phục backup, xoay vòng key, xử lý khi Zalo/AI sập.

🧑 Đăng ký webhook URL production trong Zalo App; tạo key production.

**Tiêu chí xong:** staging và production chạy; khôi phục backup thành công; cảnh báo lỗi về điện thoại/email; load test đạt.

---

## Phase 8 — Dùng thử thật (pilot)

**Mục tiêu:** 2–3 DN dùng với khách thật, sửa lỗi, lấy số liệu để định giá.

🧑 Việc của bạn:
- Hướng dẫn DN: kết nối OA, nạp dữ liệu, cài đặt giọng văn, thêm nhân viên.
- Đọc hội thoại hằng ngày (tuần đầu), gom câu bot trả lời sai.
- Gặp DN cuối mỗi tuần, ghi nhận phản hồi.

🤖 Việc của Claude:
- Điều tra câu sai (skill `debug-bot-answer`), sửa đúng tầng gây lỗi, thêm vào bộ eval.
- Sửa lỗi, cải thiện hiệu năng theo số liệu thật.
- Báo cáo tuần: số hội thoại, tỷ lệ bot tự xử lý, chi phí AI trung bình/hội thoại, lỗi.

🧑 Chốt bảng giá gói (dựa trên chi phí thật + giá trị DN nhận được).

**Tiêu chí xong:** chạy ổn ≥ 2 tuần không mất tin nhắn, không lộ dữ liệu; tỷ lệ bot tự xử lý đạt mức bạn chốt; ít nhất 1 DN nói sẵn sàng trả tiền.

---

## Song song từ Phase 0 — Pháp lý và thương mại 🧑

Việc này mất thời gian (chờ luật sư, chờ duyệt) nên **bắt đầu sớm**, không đợi code xong.

| Việc | Khi nào bắt đầu | Cần xong trước |
| --- | --- | --- |
| Đăng ký kinh doanh (hộ kinh doanh hoặc công ty) | Phase 1–2 | Phase 9 (xuất hóa đơn, ký hợp đồng) |
| Thuê luật sư: điều khoản sử dụng, chính sách bảo mật, hợp đồng dịch vụ, hợp đồng xử lý dữ liệu cá nhân (DN là bên kiểm soát, bạn là bên xử lý) | Phase 3 | Phase 8 |
| Hồ sơ đánh giá tác động xử lý dữ liệu cá nhân | Phase 4 | Phase 8 |
| Hồ sơ chuyển dữ liệu ra nước ngoài (vì gửi nội dung tới AI ở nước ngoài) | Phase 4 | Phase 8 |
| Kiểm tra chính sách Zalo về bot, tin chủ động, ZNS | Phase 0 | Phase 4 |
| Kiểm tra quy định hiện hành về AI, quảng cáo, chống tin rác | Phase 3 | Phase 9 |
| Thanh toán: chuyển khoản thủ công + hóa đơn (tự động hóa để GĐ3) | Phase 6 | Phase 9 |
| Trang giới thiệu sản phẩm (landing page) + trang chính sách công khai | Phase 6 | Phase 9 |
| Đặt tên thương hiệu (tránh dùng "Zalo" làm tên chính) | Phase 0 | Phase 9 |

Lưu ý: danh sách căn cứ pháp lý ở mục 7 CLAUDE.md chỉ để định hướng. Luật sư phải xác nhận văn bản hiện hành.

---

## Phase 9 — Phát hành v1

**Mục tiêu:** bán cho khách trả tiền đầu tiên, vận hành bền vững.

Checklist trước khi mở bán:
- [ ] Mọi tiêu chí xong của Phase 1–8 đạt.
- [ ] Giấy tờ pháp lý đã có (bảng song song ở trên).
- [ ] Zalo App đã được duyệt cho production.
- [ ] Bảng giá + hạn mức theo gói đã cấu hình trong hệ thống.
- [ ] Quy trình nhận khách: ký hợp đồng → bạn tạo tenant (super admin) → gửi tài khoản → hướng dẫn kết nối OA → nạp dữ liệu → chat thử → bật bot.
- [ ] Tài liệu hướng dẫn cho DN (có ảnh chụp màn hình): kết nối OA, nạp dữ liệu, tiếp quản chat, xem lead.
- [ ] Kênh hỗ trợ khách (Zalo OA của bạn hoặc nhóm chat) + cam kết thời gian phản hồi.
- [ ] Quy trình khi DN ngừng thuê: xuất dữ liệu cho DN → xóa toàn bộ dữ liệu tenant.
- [ ] Theo dõi chi phí AI/tháng, cảnh báo khi vượt ngưỡng.
- [ ] Gắn tag phiên bản `v1.0.0` ở cả 2 repo.

Sau phát hành (lặp lại hằng tuần):
- Đọc Sentry + câu bot không trả lời được → sửa, thêm eval.
- Xem chi phí AI từng tenant, so với giá gói.
- Ghi yêu cầu tính năng của khách → xếp vào Giai đoạn 2.

**Tiêu chí xong:** ≥ 1 khách trả tiền, chạy ổn 1 tháng.

---

## Sau v1

### Giai đoạn 2 (~1–2 tháng, khi đã có khách trả tiền)
- Flow chăm sóc chủ động do bot gửi (tình huống mẫu ở `FEATURE-SPECS.md` mục 4, flow mẫu theo ngành ở `INDUSTRIES.md`): chào khách mới, khách chưa chốt, nhắc lịch, hỏi thăm sau dịch vụ + gợi ý gói, xin đánh giá, sinh nhật, kéo khách cũ, xử lý phàn nàn.
- Trang "Cần chăm sóc" cho phép bot tự nhắn các trường hợp đơn giản, trường hợp quan trọng vẫn để nhân viên.
- Bắt buộc kèm theo: ghi nhận đồng ý nhận tin, nhắn "hủy" là dừng, khung giờ và tần suất gửi cài cứng, ZNS cho tin ngoài khung.
- Mẫu ngành đầy đủ, chọn ngành khi tạo tenant; nhiều nhân viên, chia hội thoại; nhãn tự động; đồng bộ Google Sheets.
- Đặt lịch hẹn qua chat + nhắc lịch; hiểu ảnh khách gửi; giờ làm việc; nạp dữ liệu từ link website; báo cáo tuần tự động.

### Giai đoạn 3 (~1–2 tháng)
- Trình kéo thả thiết kế flow.
- Thanh toán tự động (QR chuyển khoản, cổng thanh toán), hóa đơn điện tử.
- Zalo Mini App, PWA cho portal.
- Kênh Facebook Messenger, chat trên website (adapter mới trong `ZaloAi.Channels`).

---

## Rủi ro lớn và cách giảm

| Rủi ro | Ảnh hưởng | Cách giảm |
| --- | --- | --- |
| Zalo duyệt app chậm hoặc từ chối | Trễ Phase 4, 9 | Nộp sớm từ Phase 0; Phase 1–3 không phụ thuộc Zalo (dùng webchat) |
| Bot trả lời sai/bịa | Mất uy tín, rủi ro pháp lý | Cổng chất lượng Phase 3, bộ eval, quy tắc "không chắc thì chuyển người" |
| Lộ dữ liệu giữa các tenant | Nghiêm trọng nhất | 2 lớp lọc tenant + test cô lập ở mọi module |
| Chi phí AI cao hơn giá bán | Lỗ | Đo từ Phase 3, ghi chi phí mọi lần gọi, hạn mức theo gói, tóm tắt hội thoại |
| Thiếu DN dùng thử | Không có dữ liệu thật, không kiểm chứng được | Tìm từ Phase 0; dùng dữ liệu mẫu trong lúc chờ |
| Làm một mình, quá tải vận hành | Chậm phản hồi khách | Cảnh báo tự động, RUNBOOK, giới hạn số khách giai đoạn đầu |
| Giấy tờ pháp lý chưa xong khi muốn bán | Không mở bán được | Làm song song từ sớm |

---

## Các điểm bạn phải quyết định (theo thứ tự)

1. **Phase 0:** câu hỏi B1–B5; ngành đầu tiên (chọn từ `INDUSTRIES.md`, rủi ro cao thì cần tài liệu chuyên môn đã duyệt); DN dùng thử.
   Các mục "(đề xuất)" trong `FEATURE-SPECS.md`: tin nháp AI, gán nhân viên, nhắc lịch ở GĐ2 hay v1.
2. **Phase 3:** mức eval đạt; đối chiếu chi phí giữa các model AI.
3. **Phase 5:** thời gian X phút nhắc nhân viên; nội dung câu chuyển tiếp.
4. **Phase 6:** gói dịch vụ, hạn mức, xử lý khi hết hạn mức.
5. **Phase 7:** nơi deploy, dịch vụ DB, gói AI trả phí.
6. **Phase 8:** giá bán.
7. **Phase 9:** thời điểm mở bán.
