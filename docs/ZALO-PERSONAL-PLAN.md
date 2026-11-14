# Kế hoạch kênh Zalo cá nhân (thiết kế đến hoàn thiện)

> Trạng thái: **ĐÃ DUYỆT 2026-11-12, ĐANG LÀM** (BE Z1–Z4 xong 2026-11-13; ghi chú triển khai ở mục 15). Chủ dự án yêu cầu 2026-11-12: bot chăm sóc khách **qua Zalo cá nhân** của doanh
> nghiệp, gồm cả **tự nhắn trước** cho khách, mỗi nick một **proxy riêng**. Chủ dự án đã hỏi luật sư.
> Bổ sung cùng ngày: **tự gửi lời mời kết bạn kèm lời chào, và tin chào mời**: chỉ cho **khách của chính DN** (mục 6.7).
> Bổ sung tiếp: DN đưa danh sách SĐT (khách cũ **và người đã hỏi / để lại thông tin**) → tìm nick, kết bạn, chăm sóc, đưa thành
> khách tiềm năng, giới thiệu liệu trình, **tự chốt lịch**: dùng chung mọi flow của OA (mục 6.8).
> Nguồn chính chủ dự án nêu: **khách giới thiệu người quen**, đưa SĐT người quen cho DN tư vấn. Giao diện chốt: **một ô dán SĐT
> hàng loạt** (không có nút giới thiệu riêng) + màn hình theo dõi ai đang được chăm sóc, chat đủ tính năng như OA (mục 6.9, 9).
> Làm **trước C7** trên nhánh `feat/zalo-personal` (BE + FE, tách từ `dev`), mỗi đợt test xong thì PR vào `dev`.
> Thư viện: `zca-js` (không chính thức, giả lập Zalo Web). Các điểm về zca-js trong file này đọc từ **mã nguồn bản 2.2.0**
> (npm, MIT, Node ≥ 18, cập nhật 2026-09-09), không đoán; điểm chưa kiểm chứng ghi rõ "chưa kiểm chứng".

---

## 0. Cần chủ dự án chốt trước khi code

| # | Việc | Đề xuất của Claude |
| --- | --- | --- |
| 1 | "Bỏ cờ pháp lý" | Bỏ nguyên tắc 2 cũ và các ghi chú "cần luật sư xác nhận" trong docs + hồ sơ gọi vốn. **Giữ trong code**: "hủy" là dừng, khung 07:00–21:00, một tin chờ trả lời, cách 24 giờ (giờ là luật giữ nick khỏi bị báo xấu). Giữ bộ an toàn y tế (an toàn người bệnh, không phải cờ pháp lý). |
| 2 | Tin quảng cáo / chào mời khi khách chưa đồng ý nhận tin | **Giữ** luật hiện tại: bot tự gửi chào mời chỉ cho khách đã đồng ý; khách chưa đồng ý → tin nháp vào "Cần chăm sóc", nhân viên duyệt (chọn nhiều rồi duyệt một lần được). |
| 3 | Kế hoạch Z1–Z4, schema, thư viện ở dưới | Duyệt. |
| 4 | Kết bạn + chào mời gửi cho ai | Người **đã có quan hệ với DN**: khách đã dùng dịch vụ, người đã hỏi / để lại thông tin (form, quảng cáo, gọi điện, đến hỏi), người được giới thiệu. DN chọn **nguồn** và **xác nhận** khi nhập danh sách; hệ thống ghi lại. **Không làm** tính năng tìm người lạ (quét thành viên nhóm, gợi ý kết bạn của Zalo, dò dải số, danh sách mua). Mục 6.7–6.8. |
| 5 | Câu báo "trợ lý AI" trong tin đầu tiên | **Giữ** trên Zalo cá nhân: khách cần biết mình đang nói chuyện với trợ lý tự động. Bot vẫn xưng là người của DN. |

Nguyên tắc 2 trong CLAUDE.md đổi thành (đề xuất):
> 2. **Zalo OA qua API chính thức là kênh chính.** Kênh **Zalo cá nhân** (thư viện không chính thức `zca-js`) là kênh phụ: DN tự
> bật và tự xác nhận rủi ro nick có thể bị Zalo khóa. Không bao giờ hỏi mật khẩu Zalo (chỉ đăng nhập bằng QR). Không nhắn hàng
> loạt; chỉ kết bạn / nhắn chào với khách của chính DN, có giới hạn mỗi ngày; không tìm hay nhắn người lạ ngoài danh sách
> khách; không trả lời trong nhóm; không đọc/lưu chuyện riêng của chủ nick.

---

## 1. Mục tiêu và phạm vi

**Làm:**
- DN kết nối 1 hay nhiều nick Zalo cá nhân bằng **quét QR** trên trang Kênh.
- Tin khách nhắn vào nick → bot trả lời 24/7 bằng đúng lõi AI hiện có (kho kiến thức, chuyển nhân viên, câu cấm, an toàn y tế).
- Nhân viên xem và trả lời trong Hộp thư như kênh OA; chủ nick vẫn nhắn tay trên điện thoại được (bot tự lùi).
- **Bot tự nhắn chăm sóc** qua nick cá nhân: dùng lại toàn bộ luồng chăm sóc chủ động; thêm khả năng nhắn cho khách **chỉ có SĐT**
  (DN nhập danh sách) bằng cách tìm nick Zalo theo SĐT.
- **Tự gửi lời mời kết bạn kèm lời chào** cho khách của DN mà nick chưa nhắn trực tiếp được; tin **chào mời** theo luật đồng ý
  nhận tin (mục 6.7).
- Chống khóa nick: proxy IP riêng cố định cho từng nick, dấu vân tay thiết bị cố định, giới hạn gửi, gom tin, trả lời có độ trễ
  như người thật.
- Chế độ **giả lập** để làm và test toàn bộ khi chưa có nick thật.

**Không làm (cố ý):** nhắn hoặc kết bạn với người **không có trong danh sách khách** của DN; tìm người mới từ nguồn ngoài (quét
thành viên nhóm, gợi ý kết bạn của Zalo, dò dải số, danh sách mua); trả lời trong nhóm; đọc lịch sử chat cũ trước lúc kết nối;
chạy nhiều bản sidecar chia tải (để sau khi có nhiều DN); proxy SOCKS5 (thêm khi nhà cung cấp bắt buộc).

---

## 2. Rủi ro đã chấp nhận và cách giảm

| Rủi ro | Cách giảm trong thiết kế |
| --- | --- |
| Zalo khóa nick (README zca-js tự cảnh báo) | Proxy riêng cố định, userAgent cố định, giới hạn gửi, gom tin, độ trễ, giữ "hủy" + khung giờ; DN xác nhận rủi ro khi kết nối; test bằng nick phụ |
| Server giữ cookie = toàn quyền nick | Mã hóa AES-256-GCM, chỉ giải mã khi giao cho sidecar qua cổng nội bộ có chữ ký; không log; ngắt kết nối là xóa hẳn |
| Đọc phải chuyện riêng (gia đình, bạn bè) | Nhóm bỏ qua; bạn bè mặc định chỉ lưu tên, **không lưu nội dung, không gửi AI** tới khi chủ bấm "Đây là khách" |
| Zalo đổi giao thức → hỏng cùng lúc | Lớp dịch `translate.ts` cô lập zca-js; theo dõi bản mới zca-js; trạng thái + cảnh báo rõ khi hỏng |
| Gửi nhiều lời mời kết bạn bị bỏ qua / bị chặn (kiểu hành vi dễ bị hạn chế nhất) | Chỉ khách của DN; 10 lời mời/ngày, cách nhau ≥ 10 phút; mỗi người 1 lần; 14 ngày không chấp nhận là thôi; bị chặn là không bao giờ liên hệ lại |
| Mở Zalo Web là bot bị ngắt (mã 3000) | Phát hiện mã đóng kết nối, báo chủ, **không tự giành lại** (tránh đá phiên Zalo Web của chủ), có nút "Kết nối lại" |
| Lệch với hồ sơ gọi vốn | Sửa câu "không dùng Zalo cá nhân nên không sợ bị khóa" khi đợt Z1 bắt đầu |

---

## 3. Kiến trúc

```
                    ┌──────────────────── Node sidecar: connectors/zalo-personal ────────────────────┐
Zalo (nick cá nhân) │ Supervisor ──fork──▶ Worker nick A (zca-js, proxy A)  ── listener WebSocket      │
   ⇅ qua proxy      │            ──fork──▶ Worker nick B (zca-js, proxy B)                              │
                    │ HTTP nội bộ :4100 (chữ ký HMAC) ◀── lệnh: đăng nhập QR, gửi tin, tìm SĐT, dừng   │
                    └───────────────┬──────────────────────────────────────────────▲─────────────────┘
                                    │ sự kiện (tin vào, trạng thái, kết quả QR)     │ lệnh
                                    ▼                                               │
          API .NET — cổng nội bộ :4001 (/internal/zalo-personal/*, chữ ký HMAC)     │
                                    │ lưu tin (lọc tenant) → Hangfire                │
                                    ▼                                               │
          Worker .NET: ProcessIncomingMessageJob / chăm sóc chủ động ── ZaloPersonalAdapter (IChannelAdapter)
```

- **.NET giữ toàn bộ dữ liệu và luật** (tenant, mã hóa, AI, chăm sóc). Sidecar **không** kết nối Postgres, không có khóa mã hóa,
  không biết tenant là gì ngoài `connectionId`.
- Sidecar chỉ giữ phiên đăng nhập **trong bộ nhớ** của tiến trình con từng nick.

### Kỹ thuật dùng (để học qua code)

| Kỹ thuật | Ý tưởng | Áp vào Remi |
| --- | --- | --- |
| **Sidecar** | Tiến trình phụ chạy cạnh ứng dụng chính, làm một việc chuyên biệt | zca-js chỉ có JavaScript → Node chạy cạnh API .NET, chỉ lo kết nối Zalo |
| **Anti-corruption layer** | Lớp dịch ngăn mô hình bên ngoài "ăn mòn" mô hình của mình | `translate.ts` đổi tin zca-js → `PersonalEvent` chung; zca-js đổi thì chỉ sửa file này |
| **Adapter** | Bọc một thứ khác giao diện cho vừa giao diện chung | `ZaloPersonalAdapter : IChannelAdapter`; worker, AI, chăm sóc không sửa |
| **Strategy + Fake** | Hai cách cài cùng một interface, chọn lúc chạy | `ZaloSession`: `RealZaloSession` (zca-js) / `FakeZaloSession` (giả lập) |
| **Supervisor** | Tiến trình cha canh tiến trình con, chết thì dựng lại | Mỗi nick 1 tiến trình con; lỗi nick A không kéo nick B; dựng lại có backoff |
| **Lease lock** | Khóa có hạn, phải gia hạn đều mới giữ được | Chỉ một sidecar được giao nick (zca-js: 1 listener/nick); lỡ chạy 2 bản cũng không giành nhau |
| **HMAC request signing** | Ký mỗi request bằng khóa chung + thời điểm | Sidecar ↔ API ký như webhook Zalo; request giả/cũ bị từ chối |
| **Idempotency key** | Gửi lại cùng một lệnh không tạo kết quả thứ hai | Lệnh gửi mang `messageId`; sidecar nhớ 1 giờ → job thử lại không gửi trùng |
| **Fixed-window rate limiter** | Đếm số lần trong mỗi khung phút/giờ/ngày | Dùng lại đúng kỹ thuật đang chặn tốc độ gửi OA (`ZaloAdapter.ThrottleAsync`), thêm khung giờ/ngày |
| **Debounce** | Đợi im lặng một lúc rồi mới xử lý một lần | Khách gửi 3 tin liền → đợi 8 giây → trả lời một lần (job đến sau thắng) |
| **Resource pool** | Kho tài nguyên dùng chung, cấp phát và thu hồi | Kho proxy: nick mới lấy proxy trống; 1 proxy ↔ 1 nick |
| **Fail-closed** | Lỗi thì đóng lại, không mở toang | Proxy chết → nick dừng, **không** tự đi thẳng bằng IP máy chủ |
| **Health check / heartbeat** | Khám định kỳ, phát hiện hỏng sớm | Proxy 5 phút/lần; sidecar báo nhịp tim; job đối soát mỗi phút |

---

## 4. Sidecar Node (`connectors/zalo-personal/` trong repo BE)

**Thư viện (cần duyệt):** `zca-js` 2.2.0, `node-fetch` (zca-js bắt buộc khi đi proxy trên Node vì `fetch` có sẵn bỏ qua `agent`),
`https-proxy-agent`; dev: `typescript`, `@types/node`, `vitest`. HTTP dùng `node:http` có sẵn. Node 24.

```
connectors/zalo-personal/
├── package.json, tsconfig.json, .env.example
├── src/
│   ├── main.ts            # Supervisor: HTTP nội bộ, đối soát tài khoản, fork/khởi động lại worker
│   ├── worker.ts          # 1 nick / 1 tiến trình con: giữ ZaloSession, nhận lệnh qua IPC
│   ├── session/
│   │   ├── zalo-session.ts   # interface ZaloSession (loginQR, loginCookie, startListener, send, findUser, typing, stop)
│   │   ├── real-session.ts   # zca-js
│   │   └── fake-session.ts   # giả lập (chỉ Development)
│   ├── translate.ts       # zca-js → PersonalEvent (lớp chống ăn mòn)
│   ├── proxy.ts           # tạo agent + polyfill node-fetch; kiểm IP ra
│   ├── api-client.ts      # gọi API .NET (ký HMAC, retry có backoff)
│   ├── hmac.ts            # ký/kiểm chữ ký
│   ├── sent-cache.ts      # nhớ messageId → msgId 1 giờ (chống gửi trùng)
│   └── log.ts             # log JSON, chặn ghi cookie/nội dung/SĐT
└── test/                  # vitest, zca-js luôn được giả lập
```

**Cấu hình zca-js bắt buộc:** `new Zalo({ selfListen: true, checkUpdate: false, logging: false, agent, polyfill })`
- `selfListen: true` để nhận tin chủ nick gửi từ điện thoại (phát hiện nhân viên nhắn tay). Mặc định zca-js là `false`.
- `checkUpdate: false`, `logging: false`: mặc định zca-js tự gọi kiểm tra bản mới và tự ghi log ra console.

**Đăng nhập** (đã đọc trong mã): `loginQR({ userAgent }, callback)` phát sự kiện `QRCodeGenerated`, `QRCodeExpired`,
`QRCodeScanned`, `QRCodeDeclined`, `GotLoginInfo { cookie, imei, userAgent }`. `imei` tính từ `userAgent` → mỗi nick giữ **một
userAgent mãi mãi**. Lần sau đăng nhập bằng `login({ cookie, imei, userAgent })`.

**Bộ nghe tin:** `listener.start({ retryOnClose: true })` (zca-js tự nối lại theo lịch Zalo cấp). Sự kiện `connected`,
`disconnected(code)`, `closed(code)`, `error`, `message`, `undo`, `friend_event`. Mã đóng: 1000 chủ động, 1006 rớt mạng,
**3000 nick vừa mở Zalo Web nơi khác**, 3003 bị đá phiên.

**Proxy:** đã đọc trong mã: listener WebSocket dùng `options.agent`; mọi lệnh HTTP đi qua `options.polyfill(url, { agent })` →
cả hai đường đều qua proxy khi truyền `polyfill: node-fetch`. Không thấy lệnh gọi mạng nào đi vòng.

**Lệnh sidecar nhận (từ API, cổng 4100, ký HMAC):**

| Lệnh | Việc |
| --- | --- |
| `POST /login-qr` `{loginId, proxy?, userAgent}` | Mở tiến trình con, chạy `loginQR`, báo tiến độ về API |
| `POST /login-qr/{loginId}/cancel` | Hủy |
| `POST /accounts/{connectionId}/start` `{session, proxy?, settings}` | Chạy nick (đối soát gọi) |
| `POST /accounts/{connectionId}/stop` | Dừng listener, xóa phiên khỏi bộ nhớ |
| `POST /accounts/{connectionId}/send` `{messageId, threadId, text, delayMs, typing}` | Báo "đang soạn" → chờ `delayMs` → gửi → `{msgId}`; nhớ `messageId` |
| `POST /accounts/{connectionId}/find-user` `{phone}` | `findUser` → `{uid, displayName}` hoặc 404 |
| `POST /proxies/test` `{proxy}` | Kiểm proxy sống + IP ra |
| `GET /health` | Danh sách nick + trạng thái |

**Đối soát (mỗi 60 giây + lúc khởi động):** sidecar gọi `GET /internal/zalo-personal/accounts?instance=<id>`; API chỉ trả danh sách
cho bản đang giữ **lease** (khóa Redis 90 giây, gia hạn mỗi lần gọi) → khởi động nick thiếu, dừng nick đã bị ngắt.

**Lưu cookie mới:** cookie có thể được Zalo làm mới trong lúc chạy → worker gửi lại phiên nếu đổi (API mã hóa lại) ngay khi nick chạy, mỗi lần nối lại và mỗi 10 phút — cookie lúc quét QR là bản trước khi đăng nhập xong, khởi động lại bằng bản đó bị Zalo từ chối (thử nick thật 2026-11-13).
Thời hạn sống thật của phiên: chưa kiểm chứng, đo ở buổi test thật.

---

## 5. Thay đổi phía .NET

### 5.1 Core
- `ChannelKind.ZaloPersonal = 3`.
- `ConnectionStatus.Disconnected` (mất kết nối tạm: mở Zalo Web nơi khác, rớt mạng quá lâu, proxy hỏng). `NeedsReauth` = cần quét lại QR.
- `ZaloPersonalOptions` (validate lúc khởi động): `SidecarUrl`, `SharedSecret` (≥ 32 ký tự), `Mode` (`real`|`fake`; **`fake` ở
  môi trường khác Development → không khởi động**), `RequireProxy` (prod `true`), `InternalPort` (4001), `MaxAccountsPerTenant` (2),
  giới hạn mặc định (mục 7), `DebounceSeconds` (8), `ReplyDelayMinMs`/`MaxMs` (3000/12000), `StaffPauseMinutes` (30),
  `ProxyCheckMinutes` (5), `ProxyCooldownDays` (7).

### 5.2 Schema (mỗi đợt một migration mới; bảng có tenant đều có test cô lập)

**Z1**
```
channel_connections: access_token_enc, refresh_token_enc, access_token_expires_at, refresh_token_expires_at → NULL được
                     (chỉ OA dùng; 9 chỗ đọc thêm kiểm tra; job làm mới token lọc channel = zalo)

zalo_personal_accounts(                       -- 1–1 với channel_connections của kênh ZaloPersonal
  connection_id uuid PK FK channel_connections ON DELETE CASCADE,
  tenant_id uuid NOT NULL (index),
  session_enc text NOT NULL,                  -- JSON {cookie, imei, userAgent} mã hóa AES-256-GCM
  settings jsonb NOT NULL DEFAULT '{}',       -- chế độ nhịp gửi safe|custom + số tùy chỉnh + phanh bật/tắt (7.2), coi bạn bè là
                                              -- khách?, phút tạm dừng, nick mặc định cho chăm sóc
  listener_state text NOT NULL,               -- connected|reconnecting|opened_elsewhere|session_expired|proxy_failing|stopped
  last_seen_at timestamptz NULL,              -- nhịp tim gần nhất từ sidecar
  session_saved_at timestamptz NOT NULL,
  created_at, updated_at)

zalo_peers(                                   -- người nhắn vào nick, để lọc chuyện riêng
  id uuid PK, tenant_id uuid NOT NULL (index), connection_id uuid FK ON DELETE CASCADE,
  external_user_id text NOT NULL,             -- uid Zalo
  display_name text NULL, is_friend bool NOT NULL,
  kind text NOT NULL,                         -- customer | pending | private
  first_seen_at, last_seen_at, decided_by_user_id uuid NULL, decided_at timestamptz NULL,
  UNIQUE (connection_id, external_user_id))
```

**Z2**
```
proxies(                                      -- BẢNG HỆ THỐNG (không thuộc tenant, như users): chỉ super admin
  id uuid PK, label text NOT NULL,
  credentials_enc text NOT NULL,              -- http(s)://user:pass@host:port mã hóa
  fingerprint text NOT NULL UNIQUE,           -- HMAC của URL chuẩn hóa: chống nhập trùng mà không phải giải mã
  country text NULL, exit_ip text NULL,
  status text NOT NULL,                       -- active | failing | cooling | retired
  failing_since timestamptz NULL, cooling_until timestamptz NULL,
  last_checked_at timestamptz NULL, last_error text NULL, created_at, updated_at)

zalo_personal_accounts: + proxy_id uuid NULL UNIQUE FK proxies ON DELETE RESTRICT   -- 1 proxy ↔ 1 nick
conversations:          + bot_paused_until timestamptz NULL                          -- nhân viên vừa nhắn tay
```

**Z3**
```
outreach_batches(                             -- mỗi lần dán SĐT
  id uuid PK, tenant_id uuid NOT NULL (index), connection_id uuid NULL FK channel_connections ON DELETE SET NULL,
  origin text NOT NULL,                       -- referral | inquiry | customer
  note_enc text NULL,                         -- ghi chú cho AI của đợt (mã hóa)
  referrer_contact_id uuid NULL FK contacts ON DELETE SET NULL, referrer_name text NULL,
  attested_by_user_id uuid NOT NULL, attested_at timestamptz NOT NULL,   -- ai bấm xác nhận nguồn
  accepted_count int, skipped_count int, created_at)

outreach_targets(                             -- mỗi người trong đợt: điều khiển hàng đợi + màn hình theo dõi
  id uuid PK, tenant_id uuid NOT NULL, batch_id uuid FK ON DELETE CASCADE, contact_id uuid FK contacts ON DELETE CASCADE,
  connection_id uuid NULL, assigned_user_id uuid NULL,     -- nick + nhân viên phụ trách
  state text NOT NULL,                        -- queued|searching|no_zalo|invited|friend|messaged|replied|declined|expired|stopped|failed
  next_action_at timestamptz NULL, friend_request_sent_at timestamptz NULL, first_message_at timestamptz NULL,
  last_error text NULL, created_at, updated_at,
  UNIQUE (contact_id) WHERE state NOT IN ('declined','expired','stopped','failed'),  -- một người chỉ một nick chăm tại một lúc
  index (tenant_id, state, next_action_at))

zalo_personal_accounts: + outreach_level text NOT NULL DEFAULT 'normal'  -- normal | slow | paused (mục 7.1)
                        + outreach_level_reason text NULL, outreach_level_changed_at timestamptz NULL, outreach_paused_until timestamptz NULL
zalo_peers: + contact_id uuid NULL FK contacts ON DELETE SET NULL      -- người này là khách nào trong CRM
contacts:   + origin text NULL      -- referral | inquiry | customer khi DN dán/nhập; khách tự nhắn tới: null (đã có quan hệ).
                                    -- Khách nhập trước đây: migration ghi 'customer' (mẫu nhập cũ là danh sách khách đã dùng dịch vụ)
            + referred_by_contact_id uuid NULL FK contacts ON DELETE SET NULL   -- người giới thiệu (cùng tenant, kiểm khi ghi)
```

**Z4**
```
quick_replies(id, tenant_id NOT NULL (index), shortcut text NOT NULL, title text NOT NULL, content text NOT NULL,
              sort_order int, created_by_user_id, created_at, updated_at, UNIQUE (tenant_id, shortcut))
```

Khách: **không đổi bảng** `contacts`. Khách nhắn nick cá nhân là `channel = ZaloPersonal`, `external_user_id = uid`.
uid của một người có giống nhau khi nhìn từ hai nick khác nhau không: **chưa kiểm chứng** (đo ở buổi test thật 1); nếu khác thì
hai nick ra hai hồ sơ, gộp được bằng SĐT như hiện nay.

### 5.3 Infrastructure / API mới

| Thành phần | Việc |
| --- | --- |
| `SidecarClient` | Gọi sidecar: HttpClientFactory + resilience (timeout, retry, circuit breaker), ký HMAC |
| `InternalRequestVerifier` | Kiểm chữ ký `X-Remi-Signature: t=<unix>,v1=<hex>` (HMAC-SHA256 của `t.method.path.body`), lệch ≤ 5 phút |
| `ZaloPersonalLoginService` | Mở phiên QR (Redis, TTL 5 phút, gắn tenant + user), nhận tiến độ, tạo kết nối khi `GotLoginInfo` |
| `ZaloPersonalIngestor` | Sự kiện từ sidecar: chống trùng (Redis + unique DB), lọc nhóm/bạn bè, lưu tin, xếp job (có gom tin) |
| `ZaloPersonalAdapter` | `IChannelAdapter`: kiểm giới hạn → gọi sidecar `/send` kèm độ trễ + "đang soạn" → lỗi phân loại tạm/vĩnh viễn |
| `PersonalSendLimiter` | Đếm khung phút/giờ/ngày + người mới/ngày + tra SĐT/ngày (Redis `IncrementAsync`, như OA) |
| `PeerService` | Phân loại người nhắn; API "Đây là khách" / "Riêng tư" (ghi audit) |
| `ProxyPoolService` (Z2) | Nhập, kiểm tra, cấp, thu hồi, nghỉ 7 ngày, ngừng dùng |
| Job `PersonalReconcileJob` | Mỗi phút: nick quá 3 phút không có nhịp tim → `Disconnected` + báo |
| Job `ProxyHealthJob` (Z2) | 5 phút/lần: kiểm proxy đang gán; hỏng 2 lần liền → `failing`, dừng nick, báo |
| Job `PersonalReachJob` (Z3) | Khách chỉ có SĐT → tìm nick Zalo → gộp hồ sơ → gửi tin chủ động |

**Cổng nội bộ:** endpoint `/internal/zalo-personal/*` chỉ map trên cổng riêng 4001 (`RequireHost("*:4001")`), không đi qua tunnel
(tunnel-dev.yml chỉ mở đường Zalo OA) hay reverse proxy công khai, và vẫn bắt buộc chữ ký HMAC.

---

## 6. Các luồng chính

### 6.1 Kết nối bằng QR
1. Chủ DN bấm "Kết nối Zalo cá nhân" → hộp cảnh báo rủi ro + ô "Tôi hiểu nick có thể bị Zalo khóa" (bắt buộc tích).
2. API kiểm: quyền chủ, số nick tối đa theo gói, (prod) còn proxy trống → **giữ chỗ proxy** → chọn userAgent cho nick → tạo phiên QR
   trong Redis (`loginId`, tenant, user, proxy, userAgent, TTL 5 phút) → gọi sidecar `/login-qr`.
3. Sidecar mở tiến trình con **đi qua đúng proxy đó**, chạy `loginQR`; mỗi sự kiện POST về `/internal/zalo-personal/login-progress`
   (ảnh QR dạng base64 lưu Redis, không lưu DB).
4. FE hỏi `GET /connections/zalo-personal/login/{loginId}` mỗi 2 giây: `waiting` (kèm QR) → `scanned` → `success` | `expired` | `declined` | `error`.
5. `GotLoginInfo` → sidecar POST `/login-result` `{loginId, uid, displayName, credentials}` → API kiểm `loginId` còn hạn và đúng
   tenant; uid đã kết nối ở **DN khác** → từ chối (unique `channel + external_id`) → tạo `channel_connections` + `zalo_personal_accounts`
   (phiên mã hóa, gán proxy) → audit log → trả `connectionId` → tiến trình con đó chạy listener luôn.

### 6.2 Tin khách nhắn vào
1. Worker nick nhận `message` → `translate.ts` → POST `/internal/zalo-personal/events`.
2. Nhóm → bỏ, không lưu. Tin do chính nick gửi (`isSelf`) → xem 6.4.
3. Người nhắn: tra `zalo_peers`:
   - chưa có + **không phải bạn bè** → `customer`;
   - chưa có + là bạn bè → `pending` (cài đặt "coi bạn bè là khách" bật thì `customer`); chỉ lưu tên + giờ;
   - `private` → bỏ, không lưu; `pending` → cập nhật giờ, không lưu nội dung.
   (Danh sách bạn bè: worker tải `getAllFriends` lúc chạy, cập nhật theo `friend_event`.)
4. `customer` → chống trùng theo `msgId` → lưu khách/hội thoại/tin (mã hóa) như OA → **xếp job sau 8 giây** (gom tin): job thấy đã có
   tin khách mới hơn thì thôi (job của tin mới sẽ trả lời gộp).
5. Ảnh/tệp/sticker: như OA hiện tại (ghi nhận + câu chuyển nhân viên). Khách thu hồi tin (`undo`): ghi nhận, không xử lý thêm ở Z1.

### 6.3 Bot / nhân viên trả lời
`MessageDeliveryService` → `ZaloPersonalAdapter`: kiểm giới hạn → tính độ trễ (≈ 2 giây + 40 ms/ký tự, kẹp 3–12 giây, lệch ngẫu nhiên
±20%; tin nhân viên gửi tay: không chờ) → sidecar báo "đang soạn" → chờ → gửi → lưu `msgId` vào `external_message_id`.
Lỗi: mất kết nối/proxy → lỗi tạm (Hangfire thử lại); khách chặn người lạ, uid sai → lỗi vĩnh viễn; vượt giới hạn giờ/ngày → mục 7.

### 6.4 Chủ nick nhắn tay trên điện thoại
Tin `isSelf` mà `msgId` **không** nằm trong danh sách sidecar vừa gửi → lưu là tin nhân viên → `bot_paused_until = now + 30 phút`
(không chuyển hẳn sang chế độ nhân viên như OA, vì chủ nick nhắn tay thường xuyên). Hết giờ, tin khách sau bot lại trả lời.
Hộp thư hiện "Bot tạm dừng đến hh:mm" + nút "Bật lại bot".

### 6.5 Mất kết nối và đăng nhập lại

| Tình huống | Trạng thái | Hệ thống làm |
| --- | --- | --- |
| Rớt mạng (1006) | `reconnecting` | zca-js tự nối lại; quá lượt → supervisor thử lại 1, 5, 15, 30 phút → `Disconnected` + báo |
| Mở Zalo Web nơi khác (3000) / bị đá (3003) | `opened_elsewhere` → `Disconnected` | **Không tự giành lại**; báo Telegram "đóng Zalo Web rồi bấm Kết nối lại" |
| Đăng nhập bằng phiên lưu thất bại | `session_expired` → `NeedsReauth` | Báo chủ quét QR lại (giữ nguyên proxy + userAgent) |
| Proxy hỏng | `proxy_failing` → `Disconnected` | Dừng nick (fail-closed), báo chủ DN "đang tạm dừng", báo super admin đổi proxy |
| Server/sidecar khởi động lại | — | Đối soát tự chạy lại mọi nick `Active` bằng phiên đã lưu |
| Ngắt kết nối | xóa | Dừng nick, **xóa phiên**, proxy vào trạng thái nghỉ 7 ngày, hội thoại cũ giữ (`connection_id` về null), audit log |

Bot dừng trả lời khi kết nối không `Active` → tin khách chưa trả lời được báo nhân viên như khi OA mất quyền.

### 6.6 Chăm sóc chủ động (Z3)
- `CareDecision.MessagingDeadline`: Zalo cá nhân **không có hạn 7 ngày** như OA → `null`. Mọi luật cứng khác giữ nguyên.
- Khách đã chat qua nick → luồng hiện có chạy y nguyên, gửi bằng đúng nick của hội thoại.
- **Khách chỉ có SĐT** (nhập danh sách): khi tới lượt chăm sóc và DN có nick cá nhân `Active` →
  `PersonalReachJob`: còn lượt tra SĐT hôm nay → `findUser(SĐT)` qua **nick mặc định cho chăm sóc** → tìm thấy → đổi hồ sơ sang
  `ZaloPersonal` (dùng lại logic gộp hồ sơ theo SĐT trong `ContactImportService.cs`), tạo hội thoại trên nick đó → còn lượt "người
  mới hôm nay" → gửi tin chủ động. Không tìm thấy / khách chặn người lạ → như hiện nay: chuyển nhân viên **gọi điện**.
- Hết lượt trong ngày → dời sang khung giờ được nhắn của ngày sau.

### 6.7 Kết bạn + lời chào / chào mời (Z3)
Dùng khi khách **của DN** chưa nhắn trực tiếp được qua nick (khách chặn tin người lạ) hoặc DN muốn khách có nick trong danh bạ
để chăm sóc lâu dài.
- **Ai được mời:** chỉ hồ sơ khách trong Remi **có SĐT** và **có quan hệ với DN**: đã tự nhắn với DN qua một kênh, hoặc DN nhập
  danh sách kèm **nguồn** (đã dùng dịch vụ / đã hỏi, để lại thông tin / được giới thiệu) và ô xác nhận "không phải danh sách mua
  hay thu thập từ nơi khác" (mục 6.8). Không có tính năng lấy người từ nguồn khác (mục 1, "Không làm").
- **Hai cách vào hàng đợi mời:**
  1. Tự động: lượt chăm sóc tới hạn (6.6) mà gửi tin thẳng bị chặn người lạ, và nick bật "Mời kết bạn khi không nhắn được".
  2. Từ ô dán SĐT hàng loạt (6.9): số nào nhắn thẳng không được thì mời kết bạn. Hàng đợi chạy dần theo giới hạn mục 7,
     không gửi dồn.
  3. **(Chủ dự án chốt 2026-11-14)** Người dán SĐT đã **trả lời** tin chào mà chưa là bạn bè → 3 phút sau tự gửi 1 lời mời
     (`OutreachGreeting.InviteAfterReply`), cùng giới hạn lời mời/ngày + khoảng cách; trạng thái giữ "Đang tư vấn". Không mời cùng
     lúc với tin chào (tốn lượt mời cho người không trả lời, lời mời bị bỏ qua nhiều dễ bị hạn chế nick).
- **Lời chào trong lời mời:** AI soạn ngắn theo hồ sơ (tên DN, cách gọi khách, dịch vụ đã dùng), qua bộ lọc câu cấm; chỉ là lời
  chào/hỏi thăm, không quảng cáo. Độ dài tối đa của lời mời Zalo: chưa kiểm chứng → cắt an toàn ~150 ký tự, đo ở buổi test thật.
- **Mỗi người 1 lần:** không gửi lại. 14 ngày không chấp nhận → `expired`, rút lời mời (`undoFriendRequest`), thôi.
  Khách từ chối (`REJECT_REQUEST`), chặn (`BLOCK`) hoặc nhắn "hủy" → ghi từ chối nhận tin vĩnh viễn (`proactive_opt_out_at`,
  nguồn `zalo_block` / `zalo_reject`), không bao giờ liên hệ lại qua bất kỳ nick nào của DN.
- **Khách chấp nhận** (`friend_event ADD`) → `accepted`, mở hội thoại trên nick đó; tin chăm sóc đang chờ (nếu có) gửi theo luồng 6.6
  (khung giờ, cách 24 giờ, 1 tin chờ trả lời).
- **Chào mời (quảng cáo, khuyến mãi, mời quay lại):** bot tự gửi **chỉ cho khách đã đồng ý nhận tin** (`consent_marketing`). Khách
  chưa đồng ý → tin nháp vào "Cần chăm sóc", nhân viên duyệt (chọn nhiều rồi duyệt một lần). Cả hai vẫn chạy trong giới hạn mục 7.
- **zca-js** (đọc khai báo bản 2.2.0): `sendFriendRequest(msg, userId)`, `getSentFriendRequest`, `getFriendRequestStatus`,
  `undoFriendRequest`; sự kiện `friend_event` có `ADD`, `REJECT_REQUEST`, `BLOCK`. Không dùng `getFriendRecommendations`
  (gợi ý người lạ).

### 6.8 Hành trình: từ danh sách SĐT đến lịch hẹn (Z3)
Từ lúc có hội thoại, mọi bước dùng **chung code với OA**. Đã đọc code: bot đặt lịch (`BotEngine` + `AppointmentService`), nhắc lịch
(`AppointmentReminderJob`), gợi ý gói/liệu trình từ danh mục C1, tầng khách tiềm năng M6 đều đi qua hội thoại +
`MessageDeliveryService`, không phụ thuộc kênh. Khác OA duy nhất: Zalo cá nhân không có hạn 7 ngày (`MessagingDeadline` = null).

| Bước | Hệ thống làm | Luật |
| --- | --- | --- |
| 1. Dán SĐT | Ô dán SĐT hàng loạt (6.9) → mỗi số thành một hồ sơ khách (`contacts.origin`) + một dòng theo dõi (`outreach_targets`), audit log | Thiếu ô xác nhận → không nhận. Ghi chú cho AI của đợt (vd "quan tâm liệu trình giảm mỡ bụng") dùng ở bước 3–4 |
| 2. Tìm + kết bạn | Tra SĐT → nick (6.6). Nhắn thẳng được → bước 3; bị chặn người lạ → lời mời kết bạn + lời chào (6.7), chấp nhận → bước 3 | Giới hạn mục 7; mỗi người mời 1 lần |
| 3. Tin đầu tiên | AI soạn: xưng là người của DN + câu báo trợ lý AI + lý do liên hệ theo nguồn ("chị có để lại thông tin hỏi về…", "chị đã dùng dịch vụ… bên em") + 1 câu hỏi mở về nhu cầu | Khách đã đồng ý nhận tin: được kèm giới thiệu liệu trình / ưu đãi. Chưa đồng ý: chỉ hỏi thăm; ưu đãi đi qua nhân viên duyệt |
| 4. Tư vấn | Khách trả lời → bot tư vấn như OA: hỏi nhu cầu, giới thiệu **liệu trình phù hợp** từ danh mục + kho kiến thức, báo giá đúng dữ liệu, an toàn y tế | Khách hỏi thì bot trả lời đầy đủ (là trả lời, không phải quảng cáo chủ động). Không chắc → chuyển nhân viên |
| 5. Khách tiềm năng | AI xếp tầng Mới → Quan tâm → Nóng (M6), lưu nhu cầu vào hồ sơ; trang Khách hàng lọc theo tầng | Nhân viên sửa tay được |
| 6. Chốt lịch | Bot đề xuất giờ → **hệ thống kiểm tra chỗ** (C2) → chốt → gửi xác nhận | AI chỉ đề xuất, hệ thống mới chốt |
| 7. Nhắc lịch, sau buổi | Nhắc lịch C2; chăm sóc sau buổi, gói sắp hết, kéo khách quay lại: C7 | Như OA |
| Khách im | Không nhắn tin thứ hai khi khách chưa trả lời; 7 ngày sau tin đầu vẫn im → vào danh sách "gọi điện" của nhân viên | Luật cứng giữ nguyên (mục 0 số 1) |

### 6.9 Ô dán SĐT hàng loạt + màn hình theo dõi (nguồn chính: khách giới thiệu người quen, Z3)
Ví dụ: khách đưa SĐT người quen cho spa; nhân viên gom lại, dán một lần, bot tự đi tìm, kết bạn, tư vấn, chốt lịch.

**Ô dán SĐT** (trang "Chăm sóc Zalo cá nhân" → "Thêm SĐT"), một lần cho cả đợt:
- Ô văn bản: mỗi dòng một SĐT, có thể thêm tên sau dấu phẩy (`0912345678, chị Hoa`). Tự bỏ số sai định dạng, số trùng.
- **Nguồn** (một ô chọn cho cả đợt, mặc định "Khách giới thiệu người quen"; khác: "Đã hỏi / để lại thông tin", "Khách cũ").
- Ghi chú cho AI (không bắt buộc): nhu cầu chung của đợt, vd "quan tâm liệu trình giảm mỡ bụng".
- Người giới thiệu (không bắt buộc): tên hoặc SĐT; có thì tin đầu nhắc "chị Lan giới thiệu chị…"; trùng SĐT khách đang có thì ghi
  `referred_by_contact_id` (C7 dùng để thưởng người giới thiệu).
- Nick phụ trách (chỉ hiện khi DN có nhiều nick; mặc định nick dành cho chăm sóc).
- **Một ô xác nhận bắt buộc**: "Các số này là người đã liên hệ, đã dùng dịch vụ hoặc được khách giới thiệu; không phải danh sách mua
  hay thu thập từ nơi khác". Đây là thứ duy nhất ngăn tính năng thành công cụ nhắn rác; hệ thống ghi ai xác nhận, lúc nào.
- Bấm "Bắt đầu chăm sóc" → báo ngay: nhận bao nhiêu số, bỏ qua bao nhiêu kèm lý do (đã là khách đang chat, đã từ chối nhận tin,
  đang được nick khác chăm sóc, sai định dạng) và **dự kiến bao lâu xong** theo giới hạn mục 7 (vd 300 số ≈ 15–30 ngày).

**Màn hình theo dõi** (trang "Chăm sóc Zalo cá nhân" → "Đang chăm sóc"):
- Mỗi người một dòng: tên/SĐT, **nick Zalo phụ trách**, **nhân viên phụ trách**, trạng thái (chờ tới lượt, đang tìm Zalo, không có
  Zalo, đã mời kết bạn, đã kết bạn, đã nhắn, đang tư vấn, đã đặt lịch, từ chối, hết hạn), tầng khách tiềm năng, lịch hẹn, lần nhắn cuối.
- Lọc theo trạng thái, nick, nhân viên, đợt dán. Thanh tiến độ: "Hôm nay đã mời 7/10, đã nhắn 12/20, còn 143 người trong hàng đợi".
- Bấm một dòng → mở hội thoại trong **Hộp thư**. Chọn nhiều → đổi nhân viên phụ trách / dừng chăm sóc.

**Tin đầu tiên** (sau khi tìm thấy nick, nhắn thẳng hoặc qua lời mời kết bạn): xưng là người của spa + câu báo trợ lý AI + lý do
liên hệ ("chị Lan giới thiệu chị…" / "chị có để lại thông tin…") + nhu cầu nếu có + **một câu hỏi xin phép**: "Chị có muốn em gửi
thông tin liệu trình phù hợp không ạ?". Tin đầu **không** kèm bảng giá hay ưu đãi (người nhận chưa đồng ý nhận quảng cáo; một bảng
giá từ nick lạ là lý do hay bị bấm báo xấu nhất).

**Sau đó:** trả lời có / hỏi thêm → bot tư vấn đầy đủ như 6.8 bước 4–7 (liệu trình, tầng khách tiềm năng, chốt lịch, nhắc lịch).
"Không" / "hủy" → dừng vĩnh viễn. Im lặng → không nhắn thêm; 7 ngày → danh sách gọi điện (nhân viên quyết định).

**Chat đủ tính năng như OA:** hội thoại Zalo cá nhân nằm chung **Hộp thư**, cùng bảng dữ liệu, nên có đủ mọi thứ Hộp thư đang có:
realtime, tiếp quản / trả lại bot, nhân viên nhắn từ hộp thư, hồ sơ khách, nhãn, ghi chú, tầng khách tiềm năng, nhật ký chăm sóc,
gợi ý "Cần chăm sóc", đặt lịch và hóa đơn từ hồ sơ. Khác OA: nhãn kênh "Zalo cá nhân · tên nick"; chủ nick nhắn tay trên điện thoại
thì bot tạm dừng 30 phút (6.4).

---

## 7. Giới hạn chống khóa (mặc định thận trọng, chỉnh theo nick; Zalo không công bố ngưỡng)

| Giới hạn | Mặc định | Vượt thì |
| --- | --- | --- |
| Tin/phút | 6 | Lỗi tạm, thử lại phút sau |
| Tin/giờ | 60 | Trả lời khách: chuyển nhân viên (lý do `send_limit`); tin chủ động: dời giờ sau |
| Tin/ngày | 200 | Như trên, dời sang ngày sau |
| Người mới nhắn lần đầu/ngày | 20 | Tin chủ động dời ngày sau |
| Tra SĐT/ngày | 30 | Dời ngày sau |
| Lời mời kết bạn/ngày | 10 | Dời ngày sau |
| Khoảng cách giữa 2 lời mời | ≥ 10 phút (cộng ngẫu nhiên 0–10 phút) | Chờ tới lượt |

Thêm: gom tin 8 giây; độ trễ + "đang soạn"; giữ "hủy", khung 07:00–21:00, 1 tin chờ trả lời, cách 24 giờ (mục 0).

Các số ở bảng trên là mức của **chế độ an toàn**. DN muốn số khác thì dùng **chế độ tùy chỉnh** (mục 7.2).

**Trần cứng hệ thống** (chủ dự án đặt trong config, DN không vượt được ở chế độ nào): 20 lời mời/ngày, 40 người mới/ngày,
300 tin/ngày mỗi nick.

### 7.1 Không chạy sát ngưỡng khóa: phanh tự động theo phản hồi của khách (Z2–Z3)
Zalo không công bố ngưỡng khóa. Ngưỡng cũng không phải một con số cố định: nó khác theo tuổi nick, lịch sử dùng, và phần lớn phụ
thuộc **người nhận bấm chặn / báo xấu**, thứ không bộ đếm nào biết trước. Muốn biết "sát ngưỡng" ở đâu thì phải để nick bị khóa mới
biết, tức là đốt nick của chính DN. Nên hệ thống **không dò ngưỡng, không tự tăng tốc tới sát giới hạn**. Hệ thống chỉ **giảm
tốc** khi khách phản hồi xấu, và chỉ tăng lại tới mức cấu hình (không bao giờ vượt).

| Mức | Khi nào (cửa sổ 7 ngày, mỗi nick) | Hệ thống làm |
| --- | --- | --- |
| Bình thường | Không có dấu hiệu dưới | Chạy theo giới hạn cấu hình |
| Chậm lại | ≥ 15% lời mời / tin đầu bị từ chối, chặn, "không", "hủy" (tính khi đã gửi ≥ 20); hoặc tỷ lệ chấp nhận kết bạn < 20% sau ≥ 20 lời mời | Giới hạn lời mời + người mới giảm một nửa; báo chủ DN "khách phản hồi kém, xem lại danh sách hoặc lời chào" |
| Dừng nhắn trước | ≥ 30% như trên; hoặc Zalo trả lỗi bất thường khi mời / nhắn người mới; hoặc phiên bị đá ≥ 2 lần/ngày | Dừng mời + nhắn trước 72 giờ (vẫn trả lời khách tự nhắn tới); báo chủ DN + super admin; chủ DN bấm mở lại sau 72 giờ |

- **Nick mới kết nối** (< 14 ngày): lời mời tối đa 3/ngày tuần đầu, 5/ngày tuần hai, sau đó mức cấu hình. Chỉ lên bậc khi đang ở
  mức "Bình thường".
- Các con số trên là **điểm xuất phát** do Claude chọn, chỉnh sau khi có số liệu thật, và chỉ chỉnh theo hướng an toàn hơn.
- **Theo dõi để biết thật:** trang super admin đếm số nick bị hạn chế/khóa (phiên hết hạn bất thường, lỗi gửi hàng loạt), tỷ lệ chấp
  nhận / trả lời / chặn của từng nick. Đây là số đo **sau khi xảy ra**, để chủ dự án biết thực tế; không dùng để đẩy giới hạn lên.

### 7.2 Hai chế độ nhịp gửi: An toàn / Tùy chỉnh (mỗi nick, chỉ chủ DN đổi, Z2–Z3)

| | An toàn (mặc định, khuyến nghị) | Tùy chỉnh |
| --- | --- | --- |
| Số mỗi ngày | Hệ thống tự đặt theo tuổi nick (lời mời 3 → 5 → 10) | DN tự nhập: lời mời kết bạn, người mới nhắn lần đầu, tổng tin; trong trần hệ thống |
| Phanh tự động (7.1) | Luôn bật | Bật mặc định; tắt được (hộp xác nhận) |
| Vượt khuyến cáo | Không xảy ra | Ô số chuyển màu cam + câu khuyến cáo ngay cạnh ô |

Đổi sang Tùy chỉnh hoặc tắt phanh: hộp xác nhận "Nick có thể bị Zalo hạn chế hoặc khóa. Remi không khôi phục được nick bị khóa."
→ ghi audit (ai, lúc nào, số cũ → số mới). Nhân viên không đổi được.

**Luôn bật ở cả hai chế độ** (giao diện liệt kê rõ, ghi "không tắt được"):
1. Mỗi người chỉ được mời kết bạn 1 lần; không nhắn tin thứ hai khi khách chưa trả lời.
2. Khách nói "hủy", từ chối hoặc chặn → không bao giờ liên hệ lại.
3. Chỉ nhắn trong 07:00–21:00 (DN thu hẹp được, không nới được).
4. Chỉ nhắn những số đã xác nhận nguồn khi dán.
5. Zalo báo lỗi hạn chế → dừng nhắn trước ngay.
6. Không vượt trần hệ thống.

**Khuyến cáo cạnh từng ô** (câu chữ trên giao diện; số là kinh nghiệm thận trọng của Remi, ghi rõ "không phải con số Zalo công bố"):

| Ô | Khuyến cáo | Câu cảnh báo khi vượt khuyến cáo |
| --- | --- | --- |
| Lời mời kết bạn/ngày | Nick dưới 1 tháng: 3–5. Nick lâu năm, dùng hằng ngày: tối đa 10 | "Trên mức này nguy cơ Zalo hạn chế nick tăng rõ, nhất là nick mới." |
| Người mới nhắn lần đầu/ngày | Tối đa 20 | "Nhắn nhiều người chưa từng nói chuyện dễ bị bấm chặn, báo xấu." |
| Tổng tin/ngày | Tối đa 200 | "Vượt mức trong ngày: khách nhắn tới được chuyển cho nhân viên trả lời." |

Hộp **"Mẹo giữ nick an toàn"** dưới các ô: dùng nick thật đã dùng lâu, có ảnh và bạn bè; lời chào nhắc tên người giới thiệu; không
dán số của người không quen biết DN; phanh báo "khách phản hồi kém" thì sửa lời chào hoặc danh sách trước khi tăng số.

**Thẻ "Chế độ an toàn đang làm gì"** (trang Kênh → nick, và đầu trang "Chăm sóc Zalo cá nhân"):
- Chế độ (An toàn / Tùy chỉnh), mức phanh hiện tại (Bình thường / Chậm lại / Dừng nhắn trước) + lý do + đến khi nào.
- Số hôm nay: đã dùng / được phép cho lời mời, người mới, tổng tin.
- Tỷ lệ 7 ngày: chấp nhận kết bạn, trả lời, từ chối/chặn.
- Bậc nick mới (vd "Nick mới tuần 1: 3 lời mời/ngày, lên 5 từ 20/11").
- Một dòng giải thích bằng lời thường: "Remi tự giảm tốc khi nhiều người từ chối, để nick của bạn không bị Zalo khóa."

---

## 8. Proxy riêng từng nick (Z2)

- **Kho proxy** (super admin): nhập URL `http(s)://user:pass@host:port`, nhãn, quốc gia → kiểm tra ngay (sống? IP ra?) → `active`.
  Dấu vân tay chống trùng. Không xóa được proxy đang gán, chỉ "ngừng dùng".
- **Cấp phát:** nick mới lấy 1 proxy `active` chưa gán (không lấy proxy đang nghỉ). **1 proxy ↔ 1 nick** (unique). Gán **trước khi
  hiện QR**: quét QR, nhận tin, gửi tin cùng một IP.
- **IP cố định:** không xoay IP. Kiểm định kỳ thấy IP ra đổi → cảnh báo super admin.
- **Fail-closed:** proxy hỏng 2 lần kiểm liên tiếp → `failing`, dừng nick, **không bao giờ đi thẳng**. Production: `RequireProxy=true`
  → không có proxy thì không cho kết nối; hết proxy trống → nút kết nối hiện "Hết IP riêng, liên hệ Remi"; còn < 2 proxy trống → báo.
- **Đổi proxy:** super admin chọn proxy mới cho nick → dừng nick → đăng nhập lại bằng phiên cũ qua proxy mới → Zalo đòi xác minh
  thì nick về `NeedsReauth` (quét QR lại). Proxy cũ vào nghỉ 7 ngày.
- **Kiểm IP ra:** sidecar gọi một dịch vụ trả IP công khai **qua proxy** (không mang dữ liệu khách).
- **userAgent cố định:** chọn từ danh sách vài userAgent trình duyệt thật lúc kết nối, lưu trong phiên, không đổi.
- **Chọn proxy để mua:** dân cư hoặc 4G tại Việt Nam, IP cố định (static/sticky). Chủ dự án khảo sát giá → tính vào giá thuê mỗi nick.

---

## 9. Màn hình (FE, nhánh `feat/zalo-personal`)

| Màn hình | Nội dung | Đợt |
| --- | --- | --- |
| Kênh → thẻ "Zalo cá nhân" | Nút kết nối, hộp cảnh báo + ô xác nhận, hộp QR (tự làm mới, hết hạn, đã quét), danh sách nick: trạng thái, "IP riêng", kết nối lại, ngắt | Z1 |
| Kênh → cài đặt nick | Chế độ nhịp gửi An toàn / Tùy chỉnh (ô số + khuyến cáo + cảnh báo cam + hộp xác nhận), phanh tự động, danh sách "Luôn bật", mẹo giữ nick; coi bạn bè là khách, phút tạm dừng, nick mặc định cho chăm sóc (7.2) | Z1–Z3 |
| Thẻ "Chế độ an toàn đang làm gì" | Ở trang nick + đầu trang Chăm sóc Zalo cá nhân: chế độ, mức phanh + lý do, số hôm nay, tỷ lệ 7 ngày, bậc nick mới (7.2) | Z2–Z3 |
| Kênh → "Người quen nhắn" | Tên + lần nhắn cuối; "Đây là khách" / "Riêng tư" | Z1 |
| Hộp thư | Đủ tính năng như OA (chung bảng hội thoại); thêm nhãn kênh "Zalo cá nhân · tên nick", "Bot tạm dừng đến hh:mm" + "Bật lại bot" | Z1–Z2 |
| Giả lập (chỉ dev) | Quét QR giả, khách giả nhắn (người lạ/bạn bè/nhóm), nhân viên nhắn từ điện thoại, sự cố (3000, phiên hết hạn, proxy chết, chặn người lạ, không thấy SĐT) | Z1–Z3 |
| Super admin → Proxy | Danh sách, thêm, kiểm tra, ngừng dùng, nick đang dùng, IP ra, trạng thái | Z2 |
| Cần chăm sóc | Nhãn kênh; lý do "đã tìm thấy Zalo", "không tìm thấy Zalo → gọi điện", "đã mời kết bạn"; chọn nhiều tin chào mời → duyệt một lần | Z3 |
| Chăm sóc Zalo cá nhân → Thêm SĐT | Ô dán SĐT hàng loạt, nguồn, ghi chú cho AI, người giới thiệu (không bắt buộc), nick phụ trách, ô xác nhận; kết quả nhận/bỏ qua + dự kiến thời gian (6.9) | Z3 |
| Chăm sóc Zalo cá nhân → Đang chăm sóc | Mỗi người một dòng: nick + nhân viên phụ trách, trạng thái, tầng, lịch hẹn, lần nhắn cuối; lọc; tiến độ hôm nay; bấm → Hộp thư; chọn nhiều → đổi nhân viên / dừng | Z3 |
| Kênh → cài đặt nick | Bật/tắt "Mời kết bạn khi không nhắn được", giới hạn lời mời/ngày | Z3 |
| Cài đặt → Tin nhắn mẫu + Hộp thư gõ "/" | Tạo/sửa/xóa mẫu, chèn nhanh | Z4 |

---

## 10. API (OpenAPI → `pnpm gen:api`) — đường dẫn THẬT đã làm (cập nhật 2026-11-13)

**Kênh (chủ DN; "Bật lại bot" cho cả nhân viên):** `GET /channels` (kèm `personal`: trạng thái, nhịp gửi, giới hạn + đã dùng hôm nay,
mức phanh, tình trạng IP) · `POST /channels/zalo-personal/login` `{connectionId?}` (có = quét lại QR cho nick cũ, giữ userAgent + proxy) ·
`GET /channels/zalo-personal/login/{loginId}` · `POST .../login/{loginId}/cancel` · `POST /channels/zalo-personal/{id}/reconnect` ·
`PUT .../{id}/settings` `{treatFriendsAsCustomers, staffPauseMinutes}` · `PUT .../{id}/pacing` `{mode, invitesPerDay?, newContactsPerDay?,
messagesPerDay?, autoBrake, confirmRisk}` · `GET .../{id}/peers?kind=` · `PUT /channels/zalo-personal/peers/{peerId}` `{kind}` ·
`DELETE /channels/{id}` (ngắt: xóa phiên, proxy nghỉ 7 ngày) · `POST /inbox/conversations/{id}/resume-bot`.

**Chăm sóc (nhân viên + chủ):** `POST /zalo-outreach/batches` `{phones, origin, note?, referrerName?, referrerPhone?, connectionId?, attested}`
→ `{batchId, accepted, skipped[{phone, reason}], etaDays}` · `GET /zalo-outreach/targets?state=&connectionId=&assignedUserId=&batchId=` ·
`PUT /zalo-outreach/targets` `{ids[], assignedUserId?, stop}` · `GET /zalo-outreach/summary`.

**Tin nhắn mẫu (Z4):** `GET /quick-replies` (nhân viên) · `POST/PUT/DELETE /quick-replies[/{id}]` (chủ DN).

**Super admin (Z2):** `GET/POST /admin/proxies` · `POST /admin/proxies/{id}/test` · `POST /admin/proxies/{id}/retire` ·
`GET /admin/zalo-personal/nicks` · `PUT /admin/zalo-personal/{connectionId}/proxy` `{proxyId}`.

**Nội bộ (cổng 4001, HMAC, ngoài OpenAPI — CONTRACT.md):** `POST /internal/zalo-personal/sync` (nhịp tim + danh sách nick + lease) ·
`POST .../login-progress` · `POST .../login-result` · `POST .../events` · `POST .../session`.

**Dev (chỉ Development + `Mode=Fake`):** `POST /channels/zalo-personal/dev/simulate` (chỉ nick / phiên QR của chính DN).

**Chưa làm so với bản thiết kế:** `POST /care/suggestions/approve` (duyệt nhiều tin chào mời một lần) — tin chào mời cho khách chưa đồng ý
vẫn duyệt từng cái ở "Cần chăm sóc" như cũ.
---

## 11. Bảo mật và vận hành

- Phiên + proxy chỉ giải mã khi giao cho sidecar qua cổng nội bộ; sidecar giữ trong RAM tiến trình con, không ghi đĩa, không log.
- Log sidecar: JSON, chỉ `connectionId`, loại sự kiện, mã lỗi; cấm cookie, imei, nội dung, SĐT (có test).
- Xóa toàn bộ dữ liệu tenant (M6) mở rộng: dừng nick, xóa phiên, thả proxy.
- Audit log: kết nối, ngắt, đổi proxy, quyết định "khách/riêng tư".
- Production: sidecar là service Docker riêng (Node 24), **1 bản**, mạng nội bộ với API; lease lock chặn việc lỡ chạy 2 bản.
- `dev.cmd` mở cửa sổ "ZaloAi Personal" (`npm run dev`), `stop.cmd` tắt.

---

## 12. Test

**.NET unit:** giới hạn gửi (từng khung, sang ngày mới), phân loại người nhắn, gom tin (job đến sau thắng), tạm dừng khi nhân viên
nhắn tay, `CareDecision` cho kênh cá nhân, chữ ký nội bộ (sai/cũ/thiếu → từ chối), options (`fake` ngoài Development → lỗi khởi động);
kết bạn: chỉ hồ sơ khách có SĐT, mỗi người 1 lần, hết hạn 14 ngày, từ chối/chặn/"hủy" → không bao giờ mời lại, giới hạn/ngày + khoảng
cách giữa 2 lời mời, chào mời chỉ tự gửi khi khách đã đồng ý; nhập danh sách thiếu xác nhận → từ chối; chỉ khách có `origin`
hoặc đã tự nhắn mới được tra SĐT / mời / nhắn trước; tin đầu tiên luôn có câu báo trợ lý AI; ô dán SĐT: thiếu xác nhận → 400,
số sai/trùng/đã từ chối/đang được nick khác chăm → bỏ qua kèm lý do, một người chỉ một dòng đang chạy; tin đầu có câu xin phép,
không kèm giá/ưu đãi; `referred_by_contact_id` / `outreach_targets` khác tenant → từ chối; cô lập tenant cho `outreach_batches`,
`outreach_targets`; cài đặt nhịp gửi: Tùy chỉnh vượt trần → 400, đổi chế độ / tắt phanh thiếu xác nhận hoặc do nhân viên → từ chối,
có audit; tắt phanh thì 6 luật "luôn bật" vẫn chạy.
**.NET integration (Testcontainers):** endpoint nội bộ chỉ nhận chữ ký đúng; sự kiện → lưu tin + xếp job, tin trùng chỉ 1 lần;
`login-result` sai tenant/hết hạn → từ chối; uid đã thuộc DN khác → từ chối; ngắt → xóa phiên; **cô lập tenant** cho
`zalo_personal_accounts`, `zalo_peers`, `quick_replies`; endpoint proxy chỉ super admin; phản hồi API tenant không bao giờ chứa
phiên/proxy.
**Sidecar (vitest, zca-js giả):** `translate.ts`, supervisor dựng lại có backoff, chống gửi trùng, chữ ký, phiên giả, log không lộ dữ liệu.
**FE:** nhãn trạng thái, hộp QR (các trạng thái), trang proxy.
**Thật (cần nick phụ / proxy):** checklist cuối Z1 và Z2 (mục 13).

---

## 13. Các đợt (≈ 18–21 ngày làm toàn thời gian)

### Z1 — Kết nối, nhận và gửi tin, lọc chuyện riêng (6–7 ngày)
BE: options, migration Z1, cổng nội bộ + HMAC, `SidecarClient`, đăng nhập QR, ingestor (tin, tin của chính nick, trạng thái),
adapter (giới hạn phút), đối soát + lease, ngắt kết nối, người quen, báo Telegram. Sidecar: khung dự án, supervisor + worker, phiên thật
+ giả, `translate.ts`, HTTP + HMAC, chống gửi trùng, log an toàn. FE: thẻ kênh + QR + cảnh báo, trạng thái, người quen, nhãn hộp thư,
trang giả lập. Docs: CLAUDE.md, DECISIONS, README, .env.example, zalo-api-notes (mục zca-js), dev/stop.
**Xong khi (giả lập):** quét QR giả → kết nối; khách lạ nhắn → bot trả lời trong hộp thư; bạn bè → "Người quen", không lưu nội dung;
nhóm → bỏ qua; nhân viên trả lời từ hộp thư; mã 3000 → trạng thái + Telegram; ngắt → xóa phiên; khởi động lại → tự chạy lại;
test cô lập tenant pass.
**Buổi test thật 1 (nick phụ):** quét QR thật; nhắn từ nick chính → bot trả lời; mở Zalo Web của nick phụ → báo mất kết nối →
kết nối lại; tắt/bật máy → tự đăng nhập; ghi lại: uid giữa hai nick, thời gian sống phiên, Zalo PC có làm rớt listener không.

### Z2 — Chống khóa nick (4–5 ngày)
Proxy (bảng, trang super admin, kiểm tra, cấp lúc QR, fail-closed, kiểm định kỳ, nghỉ 7 ngày, `RequireProxy`), userAgent cố định,
giới hạn giờ/ngày/người mới/tra SĐT + chế độ nhịp gửi An toàn / Tùy chỉnh có khuyến cáo và thẻ an toàn (7.2), gom tin, "đang soạn" + độ trễ, tạm dừng khi nhân viên nhắn tay + "Bật lại bot".
**Xong khi (giả lập):** proxy chết → nick dừng, không đi thẳng; vượt giới hạn → đúng hành vi mục 7; 3 tin liền → 1 câu trả lời;
nhân viên nhắn từ điện thoại → bot im 30 phút.
**Buổi test thật 2 (1 proxy):** IP ra đúng IP proxy; quét QR qua proxy; nhận/gửi tin qua proxy; rút proxy → nick dừng.

### Z3 — Bot tự nhắn chăm sóc, kết bạn, chào mời, hành trình đến lịch hẹn (6–7 ngày)
`CareDecision` + `ProactiveSendJob` cho kênh cá nhân, nick mặc định cho chăm sóc, `PersonalReachJob` (tìm SĐT, gộp hồ sơ, gửi),
giới hạn người mới/tra SĐT, nhãn ở "Cần chăm sóc". Kết bạn (6.7): migration Z3, hàng đợi mời + job gửi dần, lời chào do AI soạn,
xử lý `friend_event` (chấp nhận/từ chối/chặn), hết hạn 14 ngày, duyệt nhiều tin chào mời.
Hành trình (6.8): nguồn + xác nhận khi nhập, tin đầu tiên theo nguồn, 7 ngày im → danh sách gọi điện, chạy thử trọn hành trình
bằng giả lập (nhập → tìm → mời → chấp nhận → chào → khách hỏi liệu trình → bot tư vấn → tầng Nóng → chốt lịch → nhắc lịch).
Ô dán SĐT + màn hình theo dõi (6.9): `outreach_batches`/`outreach_targets`, job chạy hàng đợi theo giới hạn, tin đầu có câu xin phép,
trang "Chăm sóc Zalo cá nhân" (Thêm SĐT, Đang chăm sóc), dự kiến thời gian xong.
Phanh tự động (7.1): job tính tỷ lệ mỗi nick, đổi mức normal/slow/paused, báo chủ DN + super admin, bậc giới hạn cho nick mới, trần
cứng hệ thống, số liệu nick bị hạn chế ở trang super admin.
**Xong khi:** khách chỉ có SĐT (giả lập tìm thấy) → bot nhắn đúng khung giờ, ghi nhật ký; không tìm thấy → chuyển gọi điện;
khách nhắn "hủy" → dừng; quá 20 người mới/ngày → dời ngày sau; khách chặn người lạ → lời mời kết bạn kèm lời chào → giả lập chấp
nhận → hội thoại mở, tin chăm sóc gửi; giả lập chặn → không bao giờ mời lại; dán 25 số cần mời → hôm nay gửi 10, cách nhau ≥ 10 phút,
màn hình theo dõi đúng trạng thái từng người; giả lập 8/20 lời mời bị từ chối → nick sang "Dừng nhắn trước", vẫn trả lời khách
tự nhắn; nick mới chỉ được 3 lời mời/ngày tuần đầu; DN đặt giới hạn trên trần → API từ chối.
Test thật: bot tự nhắn và tự mời kết bạn nick chính của chủ dự án theo SĐT.

### Z4 — Tin nhắn mẫu và hoàn thiện (2 ngày)
Tin nhắn mẫu (bảng, API, trang, gõ "/" trong hộp thư); dọn giao diện; cập nhật hồ sơ gọi vốn; chạy lại toàn bộ checklist thật.
Tùy thời gian: trả lời trích dẫn (zca-js có `quote`), gửi ảnh từ hộp thư.

---

## 14. Việc chủ dự án cần làm
- Trả lời mục 0.
- Trước buổi test thật 1: SIM trả trước chính chủ + điện thoại cũ → nick Zalo phụ, dùng bình thường khoảng 1 tuần
  (ảnh, tên, vài bạn bè, vài tin). Nick chính đóng vai khách, **không** kết nối nick chính vào Remi.
- Trước buổi test thật 2: chọn nhà cung cấp proxy dân cư/4G Việt Nam IP cố định, mua 1 proxy.
- Commit FE/BE theo rule commit; push và PR vào `dev` sau mỗi đợt.

---

## 15. Ghi chú triển khai (2026-11-12 → 13) — chỗ làm khác bản thiết kế và lý do

- **Cột riêng thay cho `settings jsonb`** ở `zalo_personal_accounts` (`treat_friends_as_customers`, `staff_pause_minutes`, `pacing_mode`,
  `custom_*`, `auto_brake`, `outreach_level`...): có kiểu, có mặc định ở DB, dễ test hơn jsonb.
- **4 cột token OA giữ NOT NULL**, kênh cá nhân để rỗng (không đụng code OA); job làm mới token lọc `channel = zalo`.
- **Bot tạm im (`bot_paused_until`) làm luôn ở Z1** (thay vì Z2) vì nằm chung chỗ nhận tin; gom tin = xếp job chậm 8 giây, job xử lý tin
  đã có sẵn luật "tin mới nhất thắng".
- **Tin chào đầu + lời chào kết bạn dùng MẪU CỐ ĐỊNH** (`OutreachGreeting`), không để AI soạn: tin gửi người chưa từng nói chuyện với DN
  phải luôn báo trợ lý AI, nói lý do liên hệ, chỉ hỏi xin phép, không kèm giá — mẫu cố định bảo đảm 100%. Khách trả lời rồi AI mới tư vấn.
  Ghi chú cho AI của đợt dán được lưu thành "Ghi chú cho AI" trên hồ sơ từng người (AI dùng khi tư vấn).
- **Hồ sơ "chỉ có SĐT" đổi tại chỗ** sang kênh Zalo cá nhân khi tìm thấy nick (giữ ghi chú, nguồn, người giới thiệu). Người đó đã từng
  nhắn DN qua nick → gộp bản SĐT vào hồ sơ đang chat (`ContactPhoneLinker`) và dừng chăm sóc ("already_chatting").
- **Thứ tự liên hệ:** tìm SĐT → nhắn thẳng tin đầu → Zalo từ chối (thường là chặn tin người lạ) → mời kết bạn (một lần) → chấp nhận → tin đầu.
- **Phanh "Dừng nhắn trước" 72 giờ** hết hạn thì job tự tính lại theo số liệu 7 ngày (thay vì bắt chủ DN bấm mở lại): số liệu còn xấu thì
  vẫn chậm / dừng tiếp.
- **Khách chỉ có SĐT trong luồng "Cần chăm sóc" cũ** (ngoài ô dán SĐT) chưa tự đi tìm nick — vẫn là nhân viên gọi điện như trước. Muốn bot
  liên hệ họ qua Zalo cá nhân thì dán SĐT vào ô dán (có xác nhận nguồn).
- **Người xác nhận nguồn** (`outreach_batches.attested_by_user_id`) cho phép trống khi tài khoản bị xóa; bằng chứng còn ở audit log.
- **Khóa mã hóa đổi / dữ liệu hỏng**: đồng bộ nick bỏ qua phiên không giải mã được, cấp proxy bỏ qua proxy không giải mã được (log Error),
  không làm hỏng các nick khác.
