# Hợp đồng sidecar Zalo cá nhân ⇄ API .NET

Nguồn sự thật duy nhất cho giao tiếp giữa `connectors/zalo-personal` (Node, zca-js) và API .NET. Đổi gì ở đây thì sửa cả hai bên
và test của cả hai bên. Thiết kế tổng thể: `docs/ZALO-PERSONAL-PLAN.md`.

## 1. Mạng và chữ ký

- Sidecar nghe `http://127.0.0.1:4100` (dev). API nghe cổng nội bộ riêng `http://127.0.0.1:4001` chỉ cho `/internal/zalo-personal/*`.
  Cả hai **không** đi qua tunnel hay reverse proxy công khai.
- **Mọi request hai chiều đều ký** (trừ không có ngoại lệ nào):
  ```
  X-Remi-Signature: t=<unix giây>,v1=<hex thường>
  v1 = HMAC-SHA256(key = UTF-8(SharedSecret), message = UTF-8(`${t}.${METHOD}.${pathAndQuery}.${body}`))
  ```
  `METHOD` viết hoa; `pathAndQuery` đúng như trên dây (vd `/accounts/0190…/send`, `/internal/zalo-personal/sync`); `body` là chuỗi
  JSON gửi đi nguyên văn (GET/không body → chuỗi rỗng). Bên nhận: thiếu/sai định dạng/sai chữ ký → 401; `|now − t| > 300` → 401.
  So sánh chữ ký bằng hàm so sánh thời gian cố định.
- `SharedSecret` ≥ 32 ký tự: .NET `ZaloPersonal:SharedSecret`, Node `REMI_SHARED_SECRET`.
- JSON camelCase. Thời gian: số mili giây Unix (`ts`) hoặc ISO 8601.

## 2. Định danh

- `connectionId` (uuid) = id dòng `channel_connections` của nick. Sidecar không biết tenant.
- `loginId` (chuỗi ngẫu nhiên, ≤ 64 ký tự) = phiên đăng nhập QR đang chờ, trước khi có `connectionId`.
- `uid` = id Zalo của người dùng (chuỗi). `msgId` = id tin Zalo (chuỗi, zca-js trả số thì đổi sang chuỗi).
- `session` = `{ "cookie": <JSON cookie của zca-js, giữ nguyên>, "imei": "...", "userAgent": "..." }`. **Không bao giờ log.**
- `proxyUrl` = `http(s)://user:pass@host:port` hoặc `null`. **Không bao giờ log** (có mật khẩu).

## 3. API → sidecar

| Method + path | Body | Trả về |
| --- | --- | --- |
| `GET /health` | — | `200 {instance, mode, accounts:[{connectionId, state}], logins:[loginId]}` |
| `POST /login-qr` | `{loginId, userAgent, proxyUrl}` | `202`; tiến độ báo qua `/internal/zalo-personal/login-progress` |
| `POST /login-qr/{loginId}/cancel` | — | `204` (không có cũng 204) |
| `POST /accounts/{connectionId}/start` | `{session, proxyUrl}` | `202` (đang chạy rồi → 202, không chạy lại) |
| `POST /accounts/{connectionId}/stop` | — | `204` (dừng listener, xóa phiên khỏi bộ nhớ; không có cũng 204) |
| `POST /accounts/{connectionId}/send` | `{messageId, threadId, text, typingMs}` | `200 {msgId}` — xem mục 5 |
| `POST /accounts/{connectionId}/find-user` | `{phone}` | `200 {uid, displayName, avatarUrl}` · `404 {error:"not_found"}` |
| `POST /accounts/{connectionId}/friend-request` | `{uid, message}` | `204` |
| `POST /accounts/{connectionId}/undo-friend-request` | `{uid}` | `204` |
| `POST /proxies/test` | `{proxyUrl}` | `200 {ok, exitIp?, latencyMs?, error?}` (gọi dịch vụ trả IP công khai **qua proxy**) |
| `POST /dev/simulate` | mục 7 | `204` — **chỉ khi `REMI_MODE=fake`**, chế độ thật → 404 |

Lỗi chung của các lệnh trên nick: `409 {error:"not_running"}` (nick không chạy / chưa kết nối xong) — API coi là lỗi tạm.

## 4. Sidecar → API (`/internal/zalo-personal/*`)

| Method + path | Body | Trả về |
| --- | --- | --- |
| `POST /sync` | `{instance, running:[{connectionId, state}]}` | `200 {leaseGranted, accounts:[{connectionId, session, proxyUrl}]}` |
| `POST /login-progress` | `{loginId, state, qrPng?, error?}` | `204` · `404` nếu `loginId` không còn |
| `POST /login-result` | `{loginId, uid, displayName, avatarUrl?, session}` | `200 {connectionId}` · `409 {error:"expired"|"already_connected_elsewhere"|"limit_reached"}` |
| `POST /events` | `{connectionId, events:[PersonalEvent]}` | `204` · `404` nếu connection không còn (sidecar dừng nick đó) |
| `POST /session` | `{connectionId, session}` | `204` (cookie được Zalo làm mới) |

- **`/sync` mỗi 60 giây + lúc khởi động** là nhịp tim và "trạng thái mong muốn": sidecar chạy **đúng** danh sách `accounts` trả về
  (thiếu → khởi động, thừa → dừng). `leaseGranted=false` (bản sidecar khác đang giữ lease) → dừng mọi nick, `accounts` rỗng.
  API cập nhật `last_seen_at` của các nick trong `running`.
- `login-progress.state`: `qr` (kèm `qrPng` = PNG base64 không tiền tố) | `scanned` | `expired` | `declined` | `error` (kèm `error` ngắn).
  Sau `expired`/`declined`/`error` sidecar dừng tiến trình đăng nhập đó.
- `login-result` 200 → tiến trình đăng nhập đổi khóa sang `connectionId` và chạy listener luôn. 409 → dừng, không lưu gì.
- `/events` giữ đúng thứ tự theo từng nick; lỗi mạng / 5xx → thử lại có backoff (hàng đợi trong bộ nhớ, tối đa 5000 sự kiện, đầy thì
  bỏ cái cũ nhất kèm log lỗi). 4xx khác 404 → bỏ sự kiện đó, log mã lỗi.

### PersonalEvent

```jsonc
// Tin nhắn 1–1. Sidecar ĐÃ bỏ tin nhóm (không bao giờ gửi lên).
{ "type": "message", "msgId": "…", "threadId": "<uid người kia>", "fromUid": "…", "fromName": "…"|null,
  "isSelf": false, "isFriend": true, "kind": "text"|"image"|"file"|"sticker"|"other", "text": "…"|null, "ts": 1731400000000 }
// isSelf=true: chủ nick tự nhắn từ thiết bị khác (KHÔNG phải tin sidecar vừa gửi — sidecar giữ tin isSelf 3 giây và bỏ nếu msgId
// nằm trong danh sách vừa gửi). threadId = uid khách.
{ "type": "undo", "msgId": "…", "threadId": "…", "fromUid": "…", "ts": … }
{ "type": "friend", "event": "add"|"remove"|"reject_request"|"block"|"unblock", "uid": "…", "ts": … }
{ "type": "state", "state": "connected"|"reconnecting"|"opened_elsewhere"|"session_expired"|"proxy_failing"|"stopped",
  "code": 3000|null, "detail": "…"|null, "ts": … }
```

Ý nghĩa `state` và việc API làm:

| state | Khi nào | API |
| --- | --- | --- |
| `connected` | listener mở xong | status `active`, `listener_state=connected` |
| `reconnecting` | rớt (1006…), zca-js đang tự nối lại | giữ `active`, `listener_state=reconnecting` |
| `opened_elsewhere` | đóng mã 3000/3003 (mở Zalo Web nơi khác / bị đá) — sidecar **không tự chạy lại** | status `disconnected`, báo chủ DN |
| `session_expired` | đăng nhập bằng phiên lưu thất bại | status `needsreauth`, báo chủ DN quét QR lại |
| `proxy_failing` | không kết nối được qua proxy | status `disconnected`, báo chủ DN + super admin |
| `stopped` | dừng theo lệnh | không đổi gì |

Sidecar chỉ chạy lại nick khi `/sync` còn trả nick đó. Tiến trình con chết bất thường khi nick vẫn còn trong danh sách → dựng lại sau
1, 5, 15, 30 phút (rồi 30 phút mỗi lần); chạy ổn 10 phút thì đặt lại bộ đếm.

## 5. Gửi tin

`POST /accounts/{connectionId}/send {messageId, threadId, text, typingMs, replyTo?}`:
1. `messageId` (uuid tin trong DB) đã gửi trong 1 giờ qua → trả lại đúng `{msgId}` cũ, **không gửi lại**.
2. `replyTo` (tùy chọn, `msgId` Zalo của tin khách mà tin bot này trả lời): tiến trình nick nhớ `msgId` tin **khách** mới nhất của từng
   thread (không tính tin của chính nick). Thread đó đã có tin khách khác `replyTo` → không gửi, trả `409 superseded`. Kiểm **trước** và
   **sau** lúc "đang soạn" (khách nhắn chen trong lúc bot gõ). Chưa biết thread (vd vừa khởi động lại) → gửi bình thường. (2026-11-14)
3. `typingMs > 0` → `sendTypingEvent(threadId, User, DestType.User)` → chờ `min(typingMs, 15000)` → `sendMessage(text, threadId, User)`.
4. Ghi `msgId` vào danh sách "vừa gửi" (để bỏ tin dội về `isSelf`).

| Kết quả | Mã |
| --- | --- |
| Gửi được | `200 {msgId}` |
| Khách đã nhắn tin mới hơn `replyTo` | `409 {error:"superseded"}` → API đánh dấu tin `skipped`, không gửi lại |
| Nick không chạy | `409 {error:"not_running"}` → tạm |
| Lỗi mạng / timeout / proxy | `503 {error:"unavailable"}` → tạm |
| Zalo từ chối (khách chặn người lạ, uid sai, nick bị hạn chế…) | `422 {error:"rejected", code, reason}` → vĩnh viễn; `code` = mã lỗi zca-js/Zalo, `reason` ≤ 200 ký tự, **không** chứa nội dung tin |

## 6. Hành vi bắt buộc của sidecar

- zca-js: `new Zalo({ selfListen: true, checkUpdate: false, logging: false, agent?, polyfill? })`. Có `proxyUrl` → `agent =
  new HttpsProxyAgent(proxyUrl)`, `polyfill = node-fetch` (fetch sẵn có của Node bỏ qua `agent`). Proxy lỗi → **không** chạy thẳng.
- Mỗi nick (và mỗi phiên QR) một tiến trình con (`child_process.fork`), giao tiếp IPC; tiến trình mẹ giữ HTTP, hàng đợi sự kiện,
  danh sách "vừa gửi", lease/sync.
- Danh sách bạn bè: tải `getAllFriends` khi nick chạy, cập nhật theo `friend_event`; gắn `isFriend` cho từng tin.
- Ngay khi nick chạy (sau đăng nhập QR hoặc bằng phiên đã lưu), mỗi lần nối lại và mỗi 10 phút: lấy phiên hiện tại của zca-js; khác bản đã gửi → `POST /session`. (Cookie lúc quét QR là bản TRƯỚC khi đăng nhập xong, Zalo đổi ngay khi đăng nhập; chỉ giữ bản đó thì lần khởi động sau bị từ chối.)
- Log: JSON một dòng (`ts, level, msg, connectionId?, loginId?, code?`). **Cấm** ghi cookie, imei, userAgent, proxyUrl, nội dung
  tin, SĐT, tên khách.

## 7. Giả lập (`REMI_MODE=fake`, chỉ dev)

`FakeZaloSession` thay zca-js cho mọi việc. `POST /dev/simulate`:

| action | Thêm | Kết quả |
| --- | --- | --- |
| `scan_qr` | `loginId, uid?, displayName?` | `scanned` rồi `login-result` (uid mặc định `fake-<6 số>`) |
| `decline_qr` / `expire_qr` | `loginId` | `declined` / `expired` |
| `incoming` | `connectionId, fromUid, fromName?, text?, kind?, isFriend?, group?` | sự kiện `message` (`group=true` → bị bỏ như thật) |
| `self_message` | `connectionId, toUid, text` | sự kiện `message` `isSelf=true` |
| `friend` | `connectionId, uid, event` | sự kiện `friend` |
| `close` | `connectionId, code` (1006/3000/3003) | như listener thật đóng |
| `session_expired` | `connectionId` | `state=session_expired`, dừng nick |
| `proxy_fail` | `connectionId` | `state=proxy_failing`, dừng nick |

Giả lập khi gửi/tìm: tin có `[[blocked]]` → 422 `rejected`; có `[[down]]` → 503. `find-user` SĐT tận cùng `000` → 404, còn lại
uid `fake-<SĐT>`. `friend-request` tới uid có chữ `block` → sự kiện `friend` `reject_request` sau 1 giây; còn lại → `add` sau 2 giây.
QR giả: một ảnh PNG cố định.
