# Ghi chú API Zalo (đã kiểm chứng)

Chỉ ghi thông tin **đã kiểm tra trên docs chính thức** (developers.zalo.me) hoặc bằng thử nghiệm thật trên OA test, kèm **ngày kiểm tra** và **link nguồn**. Không ghi theo trí nhớ, blog, hay thư viện không chính thức (CLAUDE.md mục 8, skill `zalo-api-work`).

Quy ước trạng thái mỗi mục:
- `CHƯA KIỂM CHỨNG` — chưa ai xem docs; **không được code** dựa trên mục này.
- `ĐÃ KIỂM CHỨNG (YYYY-MM-DD)` — kèm link nguồn và người kiểm tra.
- Mục kiểm chứng cũ hơn 3 tháng → kiểm tra lại trước khi đụng code liên quan.

Hoàn thành file này là việc **đầu tiên của M4**, trước khi viết code Zalo.

---

## 1. Ứng dụng và quyền

- **Trạng thái:** ĐÃ KIỂM CHỨNG MỘT PHẦN (2026-10-04) — xem trực tiếp trang quản lý app trên developers.zalo.me (chủ dự án tạo app, chụp màn hình)
- App "Trợ Lý CSKH AI", App ID `35296570815181295` (công khai được), Secret lưu user-secrets `Zalo:AppSecret`. Tên app **không được chứa "Zalo"** (bị báo "Tên không hợp lệ").
- Trang app → Official Account → Thiết lập chung: Callback URL, Code Challenge, State, và danh sách quyền xin OA cấp. Quyền đã chọn (tối thiểu cho GĐ1): API "Gửi tin nhắn", "Quản lý thông tin OA", "Quản lý tin nhắn người dùng"; Webhook "Nhận sự kiện quản lý tin nhắn", "Nhận sự kiện quản lý người dùng". Bỏ: SĐT/ZNS, Template, GMF, cửa hàng, bài viết, ads, gọi thoại, mua dịch vụ (GĐ2 bật lại ZNS → OA phải cấp quyền lại).
- Có tùy chọn bảo mật "Yêu cầu kiểm tra app secret proof khi gọi api có sử dụng access token" (đang tắt; cần đọc docs trước khi bật).
- OA test: "Hộ kinh doanh Lê Văn Nhân", OA ID `2903274451691219559`, **Tài khoản xác thực**, đang hoạt động (2026-10-04). Gói OA: chưa rõ (trang cấp quyền yêu cầu "xác thực và nâng cấp gói" — kiểm khi kết nối thật).
- Còn thiếu: quy trình xét duyệt app, giới hạn số OA/app.
- Cần ghi: cách tạo Zalo App, liên kết OA, các quyền (scope) cần xin cho: nhận tin, gửi tin tư vấn, đọc thông tin người theo dõi; quy trình và thời gian xét duyệt app.
- Nguồn: _

## 2. OAuth cấp quyền OA cho app (kết nối OA của doanh nghiệp)

- **Trạng thái:** ĐÃ KIỂM CHỨNG (2026-10-04) — chủ dự án chụp/copy docs [Xác thực và ủy quyền cho Ứng dụng](https://developers.zalo.me/docs/official-account/bat-dau/xac-thuc-va-uy-quyen-cho-ung-dung-new) (OAuth v4) + trang quản lý app.
- **Điều kiện:** trang cấp quyền ghi "OA đã **xác thực** và **nâng cấp gói** sẽ được sử dụng các API sau" → OA chưa xác thực/chưa nâng gói có thể không dùng được API (kiểm tra với OA test). "Mỗi OA có giới hạn số lượng App được ủy quyền" (con số: chưa xem).
- **PKCE** (khuyến nghị dùng): `code_verifier` khác nhau cho mỗi lần, chuỗi 43 ký tự có chữ hoa, chữ thường, số; `code_challenge = Base64(SHA-256(ASCII(code_verifier)))` **không padding**. CHƯA RÕ: Base64 chuẩn hay base64url (thử thật). Docs khuyên dùng `state` (hoặc param tự định nghĩa trong redirect_uri) để biết code nào ứng với verifier nào. Verifier giữ bí mật phía server.
- **URL cấp quyền** (kiểm bằng console 2026-10-04: điền thử Code Challenge/State, console sinh link): `https://oauth.zaloapp.com/v4/oa/permission?app_id=<APP_ID>&redirect_uri=<URL>&code_challenge=<CHALLENGE>&state=<STATE>`. Không có tham số `code_challenge_method` → phương thức cố định SHA-256 như docs.
- **Callback** (GET, sau khi admin OA bấm "Cho phép"): `https://yourdomain.com/abc?code=<AUTHORIZATION_CODE>&oa_id=<OA_ID>` (+ state nếu có). Authorization code **dùng 1 lần, hiệu lực 10 phút**. Callback URL khai báo trong console.
- **Đổi code lấy token:** `POST https://oauth.zaloapp.com/v4/oa/access_token`, `Content-Type: application/x-www-form-urlencoded`, header `secret_key: <App Secret>`; body `code`, `app_id`, `grant_type=authorization_code`, `code_verifier` (bắt buộc nếu đã dùng code challenge). Response: `{ "access_token": "...", "refresh_token": "...", "expires_in": "90000" }` (expires_in là **chuỗi**, giây).
- Cách 2 (API Explorer, chỉ admin OA/app tự lấy token tay): chỉ dùng để thử, không dùng cho sản phẩm.

## 3. Access token và refresh token

- **Trạng thái:** ĐÃ KIỂM CHỨNG (2026-10-04, cùng trang docs)
- Access token: **25 giờ** kể từ lúc cấp. Refresh token: **3 tháng**, **chỉ dùng 1 lần**; dùng xong trả về refresh token mới. **Access token cũ hết hiệu lực ngay khi access token mới được tạo** → tin đang gửi bằng token cũ sẽ lỗi; phải đọc lại token sau khi refresh.
- **Làm mới:** `POST https://oauth.zaloapp.com/v4/oa/access_token`, form-urlencoded, header `secret_key`; body `refresh_token`, `app_id`, `grant_type=refresh_token`. Response giống khi đổi code. Vòng lặp lặp được vô hạn miễn còn refresh.
- Hệ quả thiết kế: Redis lock theo OA khi refresh (2 worker refresh cùng lúc → 1 cái làm hỏng refresh token của cái kia); lưu cặp token mới trong 1 transaction; refresh trước hạn (ví dụ còn < 1 giờ); refresh token sắp hết 3 tháng mà không refresh được → `needs_reauth`.
- CHƯA KIỂM CHỨNG: định dạng lỗi + mã lỗi khi code/refresh token sai, hết hạn, OA thu hồi quyền.

## 4. Webhook (Zalo gọi vào hệ thống)

- **Trạng thái:** ĐÃ KIỂM CHỨNG (2026-10-04) — chủ dự án chụp docs: [Giới thiệu về Webhook](https://developers.zalo.me/docs/official-account/webhook/tong-quan), [Sự kiện người dùng gửi tin nhắn](https://developers.zalo.me/docs/official-account/webhook/tin-nhan/su-kien-nguoi-dung-gui-tin-nhan)
- **Một Webhook URL cho cả app** (trang app → Webhook → "Thay đổi"); mọi OA đã cấp quyền cho app đều gửi về URL này → phân biệt OA bằng `recipient.id` (tin khách gửi). OA phải cấp access token cho app thì app mới nhận được sự kiện.
- Yêu cầu: HTTPS, dùng domain (không dùng host:port). Phản hồi **200 OK trong tối đa 2 giây** cho mọi sự kiện; không đáp ứng → Zalo gửi thông báo "webhook không hoạt động" về tài khoản quản lý app.
- Sau khi đặt URL, trang Webhook hiện: **OA Secret Key** (có nút Reset — khóa dùng để ký, KHÁC App Secret), công tắc **Webhook Retry**, công tắc **Lọc cú pháp** (bật → chỉ nhận tin text bắt đầu bằng "#": phải TẮT), và **bảng bật/tắt từng sự kiện** (tên sự kiện + nút Test).
- **Chữ ký:** header `X-ZEvent-Signature`: `mac = sha256(appId + data + timeStamp + OAsecretKey)`, `data` = chuỗi JSON body. CHƯA RÕ (kiểm bằng nút Test / request thật): giá trị header có tiền tố `mac=` hay chỉ chuỗi hex; `timeStamp` có đúng là trường `timestamp` trong body không; hex chữ thường hay hoa. Code sẽ ghi log *định dạng* header (không ghi giá trị) ở lần thử đầu rồi chốt.
- **Retry** (khi bật Webhook Retry): chỉ khi không mở được kết nối tới webhook; gửi lại sau 30 giây, 5 phút, 15 phút, 30 phút, 1 giờ, nội dung y hệt + header `num_retry` (số lần gửi lại). Vẫn lỗi → **webhook bị vô hiệu hóa và app bị hủy đăng ký sự kiện** của OA; sửa xong phải xin lại quyền nhận sự kiện trong trang cài đặt app.

## 5. Các loại sự kiện cần xử lý (GĐ1)

- **Trạng thái:** ĐÃ KIỂM CHỨNG cho tin khách gửi (2026-10-04, [docs](https://developers.zalo.me/docs/official-account/webhook/tin-nhan/su-kien-nguoi-dung-gui-tin-nhan)); quan tâm/bỏ quan tâm và "OA gửi tin": mới thấy trong danh sách quyền, payload CHƯA KIỂM CHỨNG.
- POST, `Content-Type: application/json`. Ví dụ:
  ```json
  { "app_id": "360846524940903967", "sender": { "id": "246845883529197922" }, "user_id_by_app": "552177279717587730",
    "recipient": { "id": "388613280878808645" }, "event_name": "user_send_text",
    "message": { "text": "message", "msg_id": "96d3cdf3af150460909" }, "timestamp": "154390853474" }
  ```
  - `sender.id`: id người dùng gửi; `recipient.id`: id OA nhận; `user_id_by_app`: id người dùng theo app; `timestamp`: chuỗi, mili giây; `message.msg_id`: id tin (→ dedupe); `message.quote_msg_id`: có khi khách bấm "Trả lời" một tin.
- `event_name` tin khách gửi: `user_send_text`, `user_send_image`, `user_send_link`, `user_send_audio`, `user_send_video`, `user_send_sticker`, `user_send_location`, `user_send_business_card`, `user_send_file` (bảng bật/tắt còn thấy `user_send_gif`, `user_received_message`, `user_seen_message`).
- Tin đa phương tiện có `message.attachments` = mảng `{ "type": ..., "payload": {...} }`: image/gif `{thumbnail, url}`; link `{thumbnail, description, url}`; audio `{url}`; video `{url, thumbnail, description}`; sticker `{url, id}`; location `{coordinates: {latitude, longitude}}` (chuỗi); file `{size, name, checksum (MD5), type, url}`.
- **OA gửi tin cho người dùng** (ĐÃ KIỂM CHỨNG 2026-10-04, [docs](https://developers.zalo.me/docs/official-account/webhook/tin-nhan/su-kien-official-account-gui-tin-nhan-cho-nguoi-dung)): Zalo gửi sự kiện khi OA gửi tin **qua Open API (tức chính bot của mình) hoặc từ cửa sổ chat OA Admin**. `sender.id` = OA, `sender.admin_id` = id admin OA đã nhắn — **chỉ có khi gửi bằng tool chat OA (oa.zalo.me/chatv2)**; `recipient.id` = user; `event_name`: `oa_send_text`, `oa_send_image`, `oa_send_gif`, `oa_send_list`, `oa_send_file`, `oa_send_sticker` (+ template: payload `zinstant_id, text, link_url, checksum`); `message.quote_msg_id` khi gửi tin tư vấn trích dẫn; cùng chữ ký X-ZEvent-Signature.
  - Hệ quả: tin bot gửi qua API cũng "dội" về webhook → **bỏ qua khi không có `admin_id`** (hoặc `msg_id` trùng message_id đã lưu). Có `admin_id` → nhân viên trả lời trong app OA → chuyển hội thoại sang `human`, lưu tin dạng staff.
- Quan tâm / bỏ quan tâm OA: payload CHƯA KIỂM CHỨNG ([trang docs](https://developers.zalo.me/docs/official-account/webhook/quan-ly/su-kien-khach-hang-quan-tam-hay-bo-quan-tam-oa) chưa xem).

## 6. Gửi tin tư vấn (trả lời khách)

- **Trạng thái:** ĐÃ KIỂM CHỨNG (2026-10-04) — chủ dự án chụp/copy [Gửi tin Tư vấn dạng văn bản](https://developers.zalo.me/docs/official-account/tin-nhan/tin-tu-van/gui-tin-tu-van-dang-van-ban), [Điều kiện gửi tin Tư vấn](https://developers.zalo.me/docs/official-account/tin-nhan/tin-tu-van/dieu-kien-gui-tin-tu-van)
- `POST https://openapi.zalo.me/v3.0/oa/message/cs`, `Content-Type: application/json`, header `access_token: <OA access token>`. Body: `{ "recipient": { "user_id": "<user id>" }, "message": { "text": "..." } }`. **text tối đa 2.000 ký tự** → câu trả lời dài phải cắt thành nhiều tin.
- Response: `{ "data": { "message_id", "user_id", "sent_time", "quota": {...} }, "error": 0, "message": "Success" }` → thành công khi `error == 0` (luôn kiểm `error`, không chỉ HTTP status). `message_id` lưu làm external id của tin bot.
- `data.quota.quota_type` cho biết nguồn quota: `reply` (trong khung 48h), `welcome_msg` (user quan tâm chưa tương tác), `sub_quota` (miễn phí theo gói, có `remain/total/expired_date`), `purchase_quota` (gói tính năng lẻ, `owner_type` OA|App, `owner_id`), `reward_quota` (redeem code); không có `quota` = **tin tính phí**.
- **Từ 1/1/2026: tin Tư vấn trong khung 48h miễn phí, không giới hạn** (`remain` luôn 8; sau 1/3/2026 bỏ `remain/total`). Bot trả lời ngay sau tin khách → luôn trong 48h. Nhân viên nhắn lại sau 48h (M5, trang "Cần chăm sóc" M6) có thể tốn quota gói hoặc tính phí → cần cảnh báo trên giao diện.
- **Điều kiện:** app có quyền gửi tin; người nhận **có tương tác với OA trong 7 ngày**; gửi được 24/24 (thông báo in-app/out-app 24/24), hiện trên mobile và PC/web. Định nghĩa "tương tác": bài Tổng quan tin nhắn (chưa xem).
- Còn có API "Kiểm tra tin tư vấn trong khung 48h" và "Kiểm tra hạn mức tin tư vấn miễn phí" (chưa xem).

## 7. Giới hạn tốc độ, mã lỗi

- **Trạng thái:** ĐÃ KIỂM CHỨNG (2026-10-04) — chủ dự án copy [Giới hạn tốc độ gọi API](https://developers.zalo.me/docs/official-account/phu-luc/gioi-han-toc-do-api), [Mã lỗi](https://developers.zalo.me/docs/official-account/phu-luc/ma-loi)
- Giới hạn theo **app**, tính theo phút, làm mới ở phút sau: Official Account API **4.000 request/phút**. Ngoài ra có giới hạn theo **OA** tùy gói OA (con số: chưa xem). Vượt → `error = -32` ("Your application/OA reached limit call api"). Mọi response có header `X-RateLimit-Limit`, `X-RateLimit-Remain`. Khuyến nghị: trải đều request, xử lý -32.
- Mã lỗi quan trọng (response `{ "error": <mã>, "message": ... }`):
  - Token: **-216** access token không hợp lệ, **-220** access token hết hạn/không còn khả dụng → refresh rồi thử lại 1 lần; vẫn lỗi → `needs_reauth`.
  - Quyền/app: **-223** OA chưa cấp quyền API này, **-219** app bị gỡ/vô hiệu hóa, **-209** app chưa kích hoạt, **-212** app chưa đăng ký API → `needs_reauth`/báo chủ dự án.
  - OA: **-204** OA bị xóa, **-205** OA không tồn tại, **-221** OA chưa xác thực, **-224** OA chưa mua gói cho tính năng này.
  - Người nhận (lỗi vĩnh viễn, không retry): **-213** chưa quan tâm OA, **-227** user bị khóa/không online > 45 ngày, **-230** không tương tác 7 ngày, **-232** chưa tương tác/tương tác hết hạn, **-244** user hạn chế loại tin, **-218** quá giới hạn gửi tới user.
  - Khác: **-32** vượt tốc độ (retry sau), **-200** gửi thất bại, **-201** tham số sai, **-210** tham số vượt giới hạn, **-211** hết quota, **-234** không gửi được 22h–6h (tin truyền thông), **-242** appsecret_proof sai, **-248** vi phạm tiêu chuẩn nền tảng, **-320/-321** tính năng trả phí cần Zalo Cloud Account / hết tiền.
- CHƯA KIỂM CHỨNG: định dạng lỗi của endpoint OAuth (`oauth.zaloapp.com/v4/oa/access_token`) khi code/refresh token sai.

## 8. ZNS (Zalo Notification Service) — Giai đoạn 2

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi: điều kiện dùng, quy trình duyệt mẫu tin, giá mỗi tin, endpoint gửi, quy định nội dung (không quảng cáo...).
- Nguồn: _

## 9. Chính sách và điều khoản

- **Trạng thái:** CHƯA KIỂM CHỨNG
- Cần ghi: quy định của Zalo về chatbot/trợ lý AI trên OA, nội dung cấm, yêu cầu báo cho người dùng là bot, quy định lưu trữ dữ liệu người dùng Zalo, điều kiện thương mại khi app phục vụ nhiều doanh nghiệp.
- Nguồn: _

---

## Nhật ký kiểm tra

| Ngày | Mục | Người kiểm tra | Ghi chú |
| --- | --- | --- | --- |
| 2026-10-04 | 5 (OA gửi tin), 7 | chủ dự án (ảnh + copy docs) | Sự kiện oa_send_*, mã lỗi, giới hạn tốc độ |
| 2026-10-04 | 6 | chủ dự án (ảnh + copy docs) | Gửi tin tư vấn văn bản, điều kiện, quota |
| 2026-10-04 | 2, 3 | chủ dự án (ảnh + copy docs) | OAuth v4: PKCE, đổi code, refresh |
| 2026-10-04 | 4, 5 | chủ dự án (ảnh chụp docs) | Webhook tổng quan + sự kiện người dùng gửi tin |
| 2026-10-04 | 1, 2, 5 (một phần) | chủ dự án (ảnh chụp console) + Claude | Trang docs developers.zalo.me hiển thị bằng JS + Cloudflare → Claude không tự đọc được, cần chủ dự án copy nội dung |
