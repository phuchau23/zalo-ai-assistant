# Sidecar Zalo cá nhân (`connectors/zalo-personal`)

Tiến trình Node chạy **cạnh** API .NET, chỉ lo một việc: giữ kết nối tới **nick Zalo cá nhân** của doanh nghiệp
(qua thư viện `zca-js`), chuyển tin vào cho API và gửi tin ra theo lệnh của API. Không có database, không có khóa
mã hóa, không biết tenant là gì ngoài `connectionId`. Thiết kế tổng thể: `docs/ZALO-PERSONAL-PLAN.md`.
**Giao tiếp với API theo đúng [`CONTRACT.md`](./CONTRACT.md)** (chữ ký HMAC hai chiều, mọi endpoint, dạng sự kiện).

## Cảnh báo rủi ro

- `zca-js` là **thư viện không chính thức** (giả lập Zalo Web). Zalo không cho phép, có thể đổi giao thức bất cứ lúc nào
  làm sidecar hỏng, và **có thể khóa nick** đang dùng. Đây là ngoại lệ đã được chủ dự án chấp nhận so với nguyên tắc
  "chỉ dùng API chính thức" (CLAUDE.md mục 2) — chỉ dùng cho kênh nick cá nhân, chủ DN phải tích đồng ý rủi ro.
- Phiên đăng nhập (cookie) cho phép làm mọi thứ với nick: chỉ nằm trong RAM của tiến trình con, **không ghi đĩa, không log**.
- Mở Zalo Web của nick ở nơi khác sẽ đá sidecar ra (`opened_elsewhere`); sidecar **không tự giành lại**.

## Chạy khi dev

```
cd connectors/zalo-personal
npm install
copy .env.example .env        # rồi điền REMI_SHARED_SECRET (giống ZaloPersonal:SharedSecret bên .NET)
npm run dev                   # node --watch src/main.ts (Node 24 chạy thẳng TypeScript, không build)
```

- `REMI_MODE=fake` (mặc định trong `.env.example`): không đụng Zalo. Dùng `POST /dev/simulate` (CONTRACT §7) để giả lập quét QR,
  khách nhắn, đóng kết nối... Ví dụ: `{ "action": "scan_qr", "loginId": "..." }`,
  `{ "action": "incoming", "connectionId": "...", "fromUid": "u1", "text": "Cho em hỏi giá" }`. Gửi tin có `[[blocked]]` → 422,
  `[[down]]` → 503. `close` mã 1006 tự nối lại sau 1 giây; 3000/3003 → `opened_elsewhere`; mã khác → coi như hết lượt nối lại
  (tiến trình con thoát, supervisor dựng lại sau 1 phút). `POST /proxies/test` ở chế độ fake: host chứa chữ `fail` → lỗi, còn lại OK.
- `REMI_MODE=real`: đăng nhập Zalo thật. Chỉ dùng với nick test.
- Mọi request vào sidecar phải có `X-Remi-Signature` (CONTRACT §1), kể cả `/health`. Ký thử bằng tay:
  `node -e "const c=require('crypto'),t=Math.floor(Date.now()/1000),b='';console.log('t='+t+',v1='+c.createHmac('sha256',process.env.REMI_SHARED_SECRET).update(t+'.GET./health.'+b).digest('hex'))"`

Kiểm tra: `npm test` (vitest, zca-js luôn được giả lập, có test e2e chạy tiến trình thật ở chế độ fake) và `npm run typecheck`.

## Biến môi trường

| Biến | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `REMI_SHARED_SECRET` | — (bắt buộc, ≥ 32 ký tự) | Khóa HMAC chung với API. Thiếu → không khởi động |
| `REMI_PORT` | `4100` | Cổng HTTP nội bộ |
| `REMI_HOST` | `127.0.0.1` | Địa chỉ nghe. Chỉ đổi khi chạy Docker trong mạng nội bộ |
| `REMI_API_URL` | `http://127.0.0.1:4001` | Cổng nội bộ của API .NET |
| `REMI_MODE` | `real` | `real` (zca-js) hoặc `fake` (giả lập, chỉ dev) |
| `REMI_INSTANCE_ID` | `<hostname>-<pid>` | Tên bản sidecar gửi trong `/sync` |
| `REMI_SYNC_INTERVAL_MS` | `60000` | Nhịp đối soát / gia hạn lease |
| `REMI_LOG_LEVEL` | `info` | `debug` \| `info` \| `warn` \| `error` |

## Cách hoạt động (tóm tắt)

- **Supervisor** (`src/main.ts`, `src/supervisor.ts`): HTTP nội bộ, hàng đợi sự kiện gửi lên API (giữ thứ tự từng nick, thử lại có
  backoff, tối đa 5000), bộ nhớ "vừa gửi" (chống gửi trùng 1 giờ, bỏ tin dội về), đối soát `/sync`.
- **Worker** (`src/worker.ts`): mỗi nick / mỗi phiên quét QR là **một tiến trình con** (`child_process.fork`). Nick A sập không kéo
  nick B. Tiến trình con chết bất thường → dựng lại sau 1, 5, 15, 30 phút (rồi 30 phút), chạy ổn 10 phút thì đặt lại.
- **Lớp dịch** (`src/translate.ts`): tin zca-js → `PersonalEvent`; tin nhóm bị bỏ ngay tại đây.
- **Proxy** (`src/proxy.ts`): có `proxyUrl` thì mọi HTTP và WebSocket của nick đi qua proxy (`node-fetch` + `https-proxy-agent`).
  Proxy hỏng → nick dừng (`proxy_failing`), **không bao giờ đi thẳng** bằng IP máy chủ.
- **Log** (`src/log.ts`): JSON một dòng, chỉ các trường cho phép; không bao giờ ghi cookie, imei, userAgent, proxyUrl, nội dung tin,
  SĐT, tên khách (có test).

## Chỉ chạy MỘT bản

zca-js chỉ cho một listener mỗi nick: hai bản sidecar cùng chạy một nick sẽ đá nhau (mã 3000). Vì vậy:
- Production: **1 bản duy nhất** (service Docker riêng, mạng nội bộ với API, không đi qua tunnel/reverse proxy công khai).
- API cấp **lease** qua `/sync`: `leaseGranted=false` → sidecar dừng mọi nick. Nếu không gia hạn được lease quá **90 giây**
  (API không trả lời), sidecar cũng tự dừng mọi nick (fail-closed) và chạy lại khi `/sync` thành công.

## Lưu ý cho phía .NET

- `POST /accounts/{id}/send` có thể mất tới `min(typingMs, 15000)` + thời gian gửi: đặt timeout HttpClient ≥ 60 giây.
- `/sync` chỉ chạy lại nick đã báo `opened_elsewhere` / `session_expired` / `proxy_failing` khi API **bỏ nick đó khỏi danh sách
  ít nhất một lần rồi trả lại**, khi `proxyUrl` đổi, hoặc khi API gọi thẳng `POST /accounts/{id}/start`.
