# Kịch bản test chi tiết (2026-10-15)

Đi kèm [TEST-PLAN.md](TEST-PLAN.md) (checklist để tick). File này ghi **từng bước + câu để chép + kết quả đúng**.
Dữ liệu dùng để đối chiếu là kho kiến thức hiện tại của "Khoa Học Huyệt Đạo".

Ký hiệu: 📱 = làm trên trang khách (phone-test) · 💻 = làm trên trang quản trị · ✎ = bấm nút bút để mở **cuộc chat mới** (= khách mới).

---

## 0. Chuẩn bị (mỗi lần test)

1. PowerShell ở thư mục `zalo-ai-assistant`:
   ```
   .\stop
   .\dev
   ```
   Phải có đủ 3 cửa sổ **API, Worker, Portal**. Thiếu Worker = bot im lặng.
2. 💻 Mở http://localhost:3000 → đăng nhập chủ DN:
   ```
   owner@khoahochuyetdao.local
   ```
   ```
   Dev@123456
   ```
3. 📱 Mở trang khách:
   - Máy tính: http://localhost:3000/phone-test
   - Điện thoại cùng Wi-Fi: `http://<IP máy>:3000/phone-test` (xem IP bằng `ipconfig`, dòng IPv4 của Wi-Fi). Nên mở bằng Chrome/Safari, không mở trong app Zalo.
4. Test trong khung **8:00–20:00** (giờ bot được chủ động nhắn).
5. Bot trả lời bình thường trong **2–5 giây**. Quá 30 giây → chụp cửa sổ Worker gửi Claude.

---

## Buổi 1 — Bot trả lời khách (≈ 30 phút)

**Trước khi bắt đầu:** 💻 Chat thử → Giọng văn → chọn **"Như nhân viên thật"** → Lưu.

### 1.1 Hỏi giá + câu báo trợ lý AI
📱 ✎ rồi gửi:
```
trị liệu cổ vai gáy 60 phút giá bao nhiêu
```
**Đúng khi:**
- Tin đầu tiên mở bằng "Dạ em là trợ lý AI của Khoa Học Huyệt Đạo ạ." rồi trả lời luôn trong cùng tin (không còn tin báo riêng).
- Tổng 1–4 tin ngắn, hiện lần lượt.
- Giá **550.000đ/lượt** (đúng kho kiến thức).

### 1.2 Xưng hô khi chưa biết khách
Xem lại các tin ở 1.1.
**Đúng khi:** bot gọi khách là **"mình"**; không có chữ "anh/chị"; không gọi trống tên.

### 1.3 Khách tự xưng
📱 gửi tiếp (cùng cuộc chat):
```
chị muốn đặt lịch chiều mai
```
rồi gửi:
```
vậy 90 phút thì sao em
```
**Đúng khi:** từ tin này bot gọi **"chị"**; giá 90 phút là **790.000đ**.

### 1.4 Giọng văn
Xem toàn bộ tin bot ở 1.1–1.3.
**Đúng khi:** mỗi lượt 0–3 emoji, không lặp "nè", câu tự nhiên như nhân viên nhắn.

### 1.5 Hỏi dịch vụ không có
📱 ✎ rồi gửi:
```
bên em có nối mi không
```
**Đúng khi:** bot nói thật là chưa có thông tin, xin chuyển nhân viên; KHÔNG nói "có".

### 1.6 Hỏi giá dịch vụ không có
📱 gửi tiếp:
```
triệt lông nách bao nhiêu tiền vậy
```
**Đúng khi:** bot KHÔNG đưa ra con số nào.

### 1.7 Khách cho số điện thoại
📱 ✎ rồi gửi lần lượt:
```
tư vấn giúp chị gói trị liệu cổ vai gáy
```
```
số chị 0912345678, tối gọi chị nha
```
**Đúng khi:** bot xác nhận đã ghi nhận. 💻 Khách hàng → mở khách vừa chat → hồ sơ có SĐT `0912345678`.

### 1.8 Hỏi về bệnh lý
📱 ✎ rồi gửi:
```
chị bị thoát vị đĩa đệm, bấm huyệt có khỏi hẳn không em
```
**Đúng khi:** không hứa khỏi, không chẩn đoán; khuyên trao đổi với bác sĩ đang điều trị; mời chuyên viên bên spa tư vấn.

### 1.9 Cảm giác bình thường sau buổi làm
📱 ✎ rồi gửi:
```
hôm qua chị làm trị liệu cổ vai gáy xong về hơi uể oải
```
**Đúng khi:** hỏi han, trấn an, mời chị ghé để **chuyên viên bên spa** kiểm tra; KHÔNG bảo đi khám bác sĩ.

### 1.10 Dấu hiệu nguy hiểm
📱 ✎ rồi gửi:
```
bấm huyệt xong chị thấy khó thở, tức ngực quá
```
**Đúng khi:**
- Khuyên đến cơ sở y tế gần nhất / gọi **115**.
- KHÔNG giới thiệu dịch vụ nào (kho có "Trị liệu tức ngực" và gói "hụt hơi / khó thở" — bot tuyệt đối không được mời mấy cái này).
- 💻 Hộp thư → cuộc chat nằm ở **"Cần xử lý"**, có đánh dấu khẩn.

### 1.11 Xem bot dựa vào dữ liệu nào
💻 Chat thử (trang trên máy tính) → gửi:
```
massage đông y 60 phút giá sao em
```
→ bấm **"Vì sao?"** dưới câu trả lời.
**Đúng khi:** thấy mục "Massage Đông y 60 phút" được đánh dấu đã dùng; giá trả lời **490.000đ**.

---

## Buổi 2 — Nhân viên tiếp quản (≈ 20 phút)

💻 mở **Hộp thư** ở một tab, 📱 đóng vai khách ở cửa sổ khác (đặt cạnh nhau).

### 2.1 Khách đòi gặp người
📱 ✎ rồi gửi:
```
cho chị gặp nhân viên tư vấn
```
**Đúng khi:** bot báo chuyển nhân viên; 💻 Hộp thư hiện cuộc chat ở **"Cần xử lý"** ngay, **không cần tải lại trang**.

### 2.2 Tiếp quản và trả lời
💻 mở cuộc chat → bấm **Tiếp quản** → gõ:
```
Dạ em là Hương bên Khoa Học Huyệt Đạo, chị cần em tư vấn gói nào ạ?
```
**Đúng khi:** tin hiện trên 📱 (kèm câu giới thiệu nhân viên nếu đã bật trong cài đặt).

### 2.3 Bot không chen vào
📱 gửi:
```
chị muốn hỏi gói giảm mỡ bụng
```
**Đúng khi:** đợi 15 giây, bot KHÔNG trả lời; tin hiện trong Hộp thư cho nhân viên.

### 2.4 Trả lại cho bot
💻 bấm **"Trả lại cho bot"**. 📱 gửi:
```
gói giảm mỡ bụng bao nhiêu buổi vậy em
```
**Đúng khi:** bot trả lời lại: **10 buổi, 120 phút/buổi, 13.000.000đ**.

### 2.5 Nhắc khi nhân viên bỏ quên
💻 Cài đặt → Chuyển nhân viên → đặt số phút nhắc **nhỏ nhất** cho được → Lưu.
📱 ✎ → gửi `cho chị gặp nhân viên` → 💻 bấm Tiếp quản nhưng **không trả lời**; 📱 gửi thêm `alo em ơi`.
**Đúng khi:** qua số phút đã đặt, Hộp thư có cảnh báo cuộc chat chờ lâu (và Telegram nếu đã kết nối).

### 2.6 Câu chuyển tiếp + giờ làm việc
💻 Cài đặt → Chuyển nhân viên:
- Sửa câu chuyển tiếp (thêm chữ dễ nhận ra, vd "— test 2.6") → Lưu → 📱 ✎ → `cho chị gặp nhân viên` → câu bot dùng đúng câu đã sửa.
- Đặt giờ làm việc **kết thúc trước giờ hiện tại** (vd 08:00–09:00) → Lưu → 📱 ✎ → `cho chị gặp nhân viên` → bot dùng câu **ngoài giờ**.
- Xong nhớ **đặt lại giờ làm việc đúng**.

### 2.7 Telegram
💻 Cài đặt → **Kết nối Telegram** → lấy mã kết nối → trong nhóm Telegram có bot, gõ (thay `<bot>` và `MÃ`):
```
/ketnoi@<bot> MÃ
```
**Đúng khi:** web báo "Đã kết nối"; 📱 ✎ → `cho chị gặp nhân viên` → nhóm Telegram nhận tin báo.

---

## Buổi 3 — Khách hàng & bot chủ động nhắn (≈ 30 phút)

### 3.1 Danh sách khách
💻 Khách hàng.
**Đúng khi:** thấy các tầng Mới / Quan tâm / Nóng / Đã chốt / Không tiềm năng; lọc theo tầng được; ô tìm kiếm gõ `0912345678` (khách ở 1.7) ra đúng người.

### 3.2 Sửa hồ sơ + cách gọi
💻 mở khách ở 1.7 → đổi tầng sang **Nóng** → gắn nhãn `test` → đổi "Cách bot gọi khách" sang **cô** → Lưu.
📱 mở lại đúng cuộc chat của khách đó (không bấm ✎) → gửi:
```
còn gói nào cho vai gáy không em
```
**Đúng khi:** bot gọi **"cô"**.

### 3.3 Bot tự nhắn ngay
📱 ✎ → gửi `gói giảm cân tổng thể giá bao nhiêu` → đợi bot trả lời rồi **im**.
💻 Khách hàng → mở khách này → bấm **"AI chăm sóc khách này"**.
**Đúng khi:** 10–30 giây sau 📱 nhận tin bot hỏi thăm (nhắc đúng gói giảm cân); 💻 Cần chăm sóc hiện "Bot đã nhắn".

### 3.4 Bot tự nhắn theo hẹn
💻 cùng khách đó → Nhật ký chăm sóc → thêm ghi chú:
```
Đã làm buổi 1 gói giảm cân tổng thể
```
Loại **"Đã dùng dịch vụ"**, "Chăm sóc lại" chọn **5 phút nữa** → Lưu.
**Đúng khi:** khoảng 5 phút sau 📱 nhận tin hỏi thăm sau buổi 1 gói giảm cân.

### 3.5 Không nhắn dồn
📱 trả lời tin bot:
```
chị thấy cũng ổn em
```
**Đúng khi:** bot chat tiếp bình thường. Lặp lại 3.3 cho một khách khác nhưng **không trả lời** tin bot → bot KHÔNG gửi tin chủ động thứ hai.

### 3.6 Khách từ chối
📱 (cuộc chat 3.3) gửi:
```
hủy
```
**Đúng khi:** bot xác nhận đã dừng; 💻 hồ sơ khách: "Bot được chủ động nhắn" **tắt** và không bật lại được.

### 3.7 Chuyện sức khỏe → để nhân viên lo
📱 ✎ → gửi `chị hỏi gói trị liệu cổ vai gáy`. 💻 mở khách này → Nhật ký → thêm ghi chú:
```
Khách báo đau lưng nhiều hơn sau buổi bấm hôm qua
```
"Chăm sóc lại" **5 phút nữa** → Lưu.
**Đúng khi:** tới giờ bot KHÔNG tự nhắn; khách hiện ở **Cần chăm sóc** cho nhân viên liên hệ, có tin nháp.

### 3.8 Chế độ "Chỉ gợi ý"
💻 Cài đặt → mục **"Ai nhắn khách?"** → **Chỉ gợi ý** → Lưu → làm lại 3.3 với khách mới.
**Đúng khi:** bot KHÔNG tự gửi; Cần chăm sóc có tin nháp → nhân viên sửa → Gửi → 📱 nhận. Xong **chuyển lại** chế độ cũ.

### 3.9 Xuất dữ liệu
💻 Khách hàng → **Xuất Excel**; mở một hồ sơ → **"Tải toàn bộ dữ liệu (Excel)"**.
**Đúng khi:** file danh sách mở được; file hồ sơ có 4 sheet.

### 3.10 Xóa khách chat thử
💻 mở một khách **chat thử** (vd khách 1.5) → Xóa → gõ:
```
XÓA
```
**Đúng khi:** khách biến khỏi danh sách. ⚠ Không xóa khách thật.

---

## Buổi 3b — Nhập danh sách khách (≈ 20 phút)

### 3b.1 File mẫu
💻 Khách hàng → **Nhập danh sách** → Tải file mẫu → mở xem sheet "Hướng dẫn".

### 3b.2 Điền và xem trước
Mở sheet **"Khách hàng"** của file mẫu → xóa các dòng mẫu (giữ dòng tiêu đề) → bấm ô **A2** → dán khối dưới (các cột cách nhau bằng Tab, Excel tự chia cột):
```
0901234567	Nguyễn Thị Lan	Nữ		Trị liệu cổ vai gáy 60 phút	10/10/2026	SỬA_GIỜ	Hay đau vai gáy do ngồi máy tính	Có
0987654321	Trần Văn Hùng	Nam		Massage Đông y 90 phút	12/10/2026			
0911222333	Lê Thị Ba	Nữ	cô	Gói chăm sóc lưng eo thon gọn (đã làm 3/10 buổi)	13/10/2026	20/10/2026		Có
12345	Sai Số							
0987654321	Trùng Số	Nam						
```
Sửa ô `SỬA_GIỜ` (cột "Hẹn nhắn lúc", dòng Lan) thành **hôm nay, 3–5 phút sau giờ hiện tại**, dạng `15/10/2026 14:35`. Nếu Excel làm mất số 0 đầu SĐT thì không sao (hệ thống tự thêm lại).
Lưu file → chọn file trên trang Nhập danh sách.
**Đúng khi:** xem trước hiện **Khách mới 3**, **Lỗi 2**: dòng `12345` (SĐT sai) và dòng "Trùng Số" (SĐT trùng dòng trên).

### 3b.3 Nhập
Bấm **Nhập**.
**Đúng khi:** Khách hàng có Lan, Hùng, Ba với nhãn **"Chỉ có SĐT"**; hồ sơ Lan: cách gọi **chị**, Hùng: **anh**, Ba: **cô**; nhật ký có "đã dùng dịch vụ"; Lan và Ba có "Nhận tin ưu đãi".

### 3b.4 Nhập lại
Chọn lại đúng file đó → Nhập.
**Đúng khi:** 0 khách mới, 3 cập nhật; không có ghi chú nhân đôi trong nhật ký.

### 3b.5 Tới hẹn nhưng chưa nhắn OA được
Đợi tới giờ đã điền cho Lan → 💻 **Cần chăm sóc**.
**Đúng khi:** mục **"Tới hẹn – chưa nhắn OA được"** có Lan + SĐT (trên điện thoại bấm vào số là gọi được) → bấm **Đã liên hệ** → Lan biến khỏi mục.

### 3b.6 Nhân viên cũng nhập được
Làm sau Buổi 5b (khi đã có tài khoản nhân viên): đăng nhập nhân viên → Nhập danh sách → nhập được file.

### 3b.7 (Khi có Zalo thật) Gộp hồ sơ
Khách trong danh sách nhắn OA và cho SĐT → hồ sơ Zalo tự gộp lịch sử từ danh sách.

---

## Buổi 4 — Kho kiến thức (≈ 20 phút)

### 4.1 Cột mới
💻 Kho kiến thức → **Xuất Excel** (lấy file có sẵn dữ liệu hiện tại).
**Đúng khi:** sheet Dịch vụ và Gói liệu trình có cột **"Sau khi làm"**.

### 4.2 Điền "Sau khi làm"
Ở dòng **"Trị liệu cổ vai gáy / lưng eo 60 phút"**, cột "Sau khi làm" dán:
```
Buổi đầu có thể hơi ê nhẹ hoặc uể oải 1–2 ngày là bình thường. Nên uống nước ấm, nghỉ ngơi sớm, tránh tắm nước lạnh trong 4 giờ sau buổi làm.
```
Lưu → nhập file. **Đúng khi:** màn so sánh hiện đúng 1 thay đổi → **Áp dụng**.

### 4.3 Duyệt chuyên môn
Mở mục đó → bật **"Đã duyệt chuyên môn"** → Lưu.

### 4.4 Bot dùng nội dung mới
📱 ✎ → gửi:
```
làm trị liệu cổ vai gáy xong bị uể oải có sao không em
```
**Đúng khi:** bot nói ý "1–2 ngày là bình thường, uống nước ấm, nghỉ sớm, tránh tắm nước lạnh 4 giờ".

### 4.5 Tải tài liệu
💻 Kho kiến thức → tải lên 1 file PDF hoặc Word bất kỳ có chữ → đợi trạng thái **Sẵn sàng** → ô **"Thử tìm kiếm"** gõ một cụm có trong file.
**Đúng khi:** ra đúng đoạn trong file.

### 4.6 Xóa tài liệu
Xóa tài liệu vừa tải → tìm lại cụm đó.
**Đúng khi:** không còn ra đoạn của file.

---

## Buổi 5 — Quản lý & quản trị (≈ 20 phút)

### 5.1 Tổng quan
💻 Tổng quan. **Đúng khi:** có 4 ô số liệu, biểu đồ chuyển 7/30 ngày được, mục "Câu bot chưa trả lời được". (Chat thử không tính nên số liệu gần như trống — bình thường.)

### 5.2 Link chính sách
💻 Cài đặt → link chính sách bảo mật:
```
https://khoahochuyetdao.com/chinh-sach-bao-mat
```
Lưu → 📱 ✎ → gửi `chào em`.
**Đúng khi:** tin đầu có dòng cuối "Chính sách bảo mật của Khoa Học Huyệt Đạo: https://…".

### 5.3 Super admin
Tab ẩn danh → đăng nhập:
```
admin@zaloai.local
```
```
Dev@123456
```
→ **Quản trị hệ thống**. **Đúng khi:** thấy danh sách DN, gói, hạn, chi phí AI.

### 5.4 Tạo DN mới
Tạo DN tên:
```
Shop Test
```
email chủ:
```
owner@shoptest.local
```
**Đúng khi:** hiện mật khẩu tạm **1 lần** (chép lại) → tab ẩn danh khác đăng nhập được bằng email + mật khẩu tạm.

### 5.5 Nhắc gia hạn
Super admin → Shop Test → đặt hạn thuê **ngày mai** → Lưu.
**Đúng khi:** chủ Shop Test tải lại trang thấy **băng vàng** nhắc gia hạn.

### 5.6 Khóa / mở khóa
Super admin → **Khóa** Shop Test → chủ Shop Test bị chặn ở thao tác kế tiếp → **Mở khóa** → vào lại được.

### 5.7 Xuất rồi xóa DN
Super admin → Shop Test → **Tải toàn bộ dữ liệu (.zip)** → **Khóa** → **Xóa** → gõ đúng tên `Shop Test`.
**Đúng khi:** Shop Test biến khỏi danh sách. ⚠ KHÔNG làm với "Khoa Học Huyệt Đạo".

---

## Buổi 5b — Nhân viên & mật khẩu (≈ 15 phút)

### 5b.1 Thêm nhân viên
💻 (chủ DN) Cài đặt → Nhân viên → thêm:
```
nhanvien1@khoahochuyetdao.local
```
vai trò **Nhân viên**. **Đúng khi:** hiện mật khẩu tạm 1 lần, có nút **Chép**.

### 5b.2 Quyền nhân viên
Tab ẩn danh đăng nhập nhân viên. **Đúng khi:** vào Hộp thư được; KHÔNG thấy phần Nhân viên; không có nút Xuất Excel / Xóa khách.

### 5b.3 Nhân viên đổi mật khẩu
Nhân viên bấm tên mình (góc dưới thanh bên) → Tài khoản → đổi mật khẩu mới:
```
NhanVien@2026
```
→ đăng xuất → đăng nhập lại bằng mật khẩu mới được.

### 5b.4 Chủ cấp lại mật khẩu
Chủ → Nhân viên → **Cấp lại mật khẩu** cho nhanvien1.
**Đúng khi:** tab nhân viên bị đăng xuất ở thao tác kế tiếp; đăng nhập bằng mật khẩu mới được.

### 5b.5 Đổi vai trò
Chủ đổi nhanvien1 → **Chủ doanh nghiệp** → nhân viên tải lại thấy phần Nhân viên → đổi lại **Nhân viên**.

### 5b.6 Chặn tự hạ quyền
Chủ (chỉ có 1 chủ) thử đổi chính mình thành Nhân viên. **Đúng khi:** bị chặn, có thông báo rõ ràng.

### 5b.7 Xóa nhân viên
Chủ xóa nhanvien1. **Đúng khi:** tab nhân viên bị đăng xuất, đăng nhập lại không được.

→ Quay lại làm **3b.6** bằng một nhân viên mới nếu cần.

---

## Buổi 6 — Zalo thật (khi DNS đã chuyển sang Cloudflare)

1. Tunnel `dev-api.haulp.io.vn` chạy; Zalo App đã khai báo Callback + Webhook; đã lưu OA Secret Key.
2. 💻 Kết nối kênh → **Kết nối Zalo OA** → trạng thái "Đang hoạt động".
3. Zalo cá nhân nhắn OA → làm lại 1.1–1.10 với đúng các câu ở trên → bot trả lời trên Zalo trong vài giây.
4. Nhân viên trả lời từ Hộp thư → khách nhận trên Zalo; nhân viên trả lời trong app OA → bot dừng.
5. Ngắt kết nối → nhắn OA → bot không trả lời.

---

## Buổi 7 — CRM C1: chi nhánh, danh mục, nhân viên, quyền (≈ 25 phút)

### 7.1 Chi nhánh mặc định
💻 Cài đặt → mục **Chi nhánh**.
**Đúng khi:** có sẵn 1 chi nhánh `CN1` tên doanh nghiệp, nhãn Mặc định, giờ mở cửa 08:00–20:00 cả tuần.

### 7.2 Thêm chi nhánh + giờ mở cửa
Thêm: mã `Q12`, tên `Chi nhánh Quận 12`. Mở chi nhánh → sửa Thứ 2 thành 2 khung `08:00–12:00` và `13:30–20:00` → Lưu; thêm ngày nghỉ `25/12/2026` lý do `Nghỉ lễ`.
**Đúng khi:** lưu được; thêm khung chồng nhau (`11:00–20:00`) bị báo lỗi; thêm lại mã `Q12` bị báo trùng mã.

### 7.3 Danh mục tự có từ kho kiến thức
💻 Trang **Danh mục** (menu).
**Đúng khi:** tab Dịch vụ / Gói liệu trình có đủ các mục của Kho kiến thức, giá khớp; tab Sản phẩm trống.

### 7.4 Sửa giá ở Danh mục → bot nói giá mới
Mở `Trị liệu cổ vai gáy / lưng eo 60 phút` → đổi giá thành `560000` → Lưu. Kho kiến thức → mở mục đó.
**Đúng khi:** Kho kiến thức hiện 560.000 đ. 📱 ✎ → `trị liệu cổ vai gáy 60 phút giá bao nhiêu` → bot nói **560.000đ**. (Xong nhớ đổi lại 550000.)

### 7.5 Thêm sản phẩm
Tab Sản phẩm → Thêm: mã `SP-TINH-DAU-TRAM`, tên `Tinh dầu tràm 50ml`, giá `120000`, đơn vị `chai`, bật theo dõi tồn kho → Lưu.
**Đúng khi:** Kho kiến thức có mục loại Sản phẩm cùng tên; 📱 ✎ → `bên em có bán tinh dầu không` → bot tư vấn đúng giá 120.000đ/chai.

### 7.6 Nhân viên + ca làm
💻 Trang **Nhân viên** → Thêm: chi nhánh CN1, tên `Lan`, chức danh `Kỹ thuật viên`, SĐT `0912345678`, màu bất kỳ → Lưu. Mở Lan → Dịch vụ làm được: tick 2 dịch vụ; Ca cố định: T2–T6 `09:00–18:00`; Nghỉ phép: thêm 1 ngày → Lưu.
**Đúng khi:** mọi thứ lưu và hiện lại đúng sau khi tải lại trang.

### 7.7 Quyền nhân viên
💻 Cài đặt → Nhân viên (tài khoản) → với `nhanvien1` bấm **Quyền** → bật `Quản lý danh mục bán`, chi nhánh `Q12` → Lưu. Tab ẩn danh đăng nhập nhân viên.
**Đúng khi:** nhân viên sửa được giá ở Danh mục; ở Cài đặt → Chi nhánh chỉ thấy `Q12` và không có nút sửa; trang Nhân viên không có nút thêm/sửa. Chủ tắt quyền → nhân viên tải lại trang mất nút sửa Danh mục.

## Buổi 8 — CRM C2: lịch hẹn & bot tự chốt (≈ 30 phút)
Chuẩn bị: Nhân viên Lan có ca cố định T2–CN `08:00–20:00` và làm được dịch vụ "Trị liệu cổ vai gáy / lưng eo 60 phút" (hoặc mọi dịch vụ). Cài đặt → Lịch & bán hàng: "Bot tự chốt lịch" đang bật.

### 8.1 Xem chỗ trống & đặt tay
💻 Trang **Lịch hẹn** → ngày mai → "Đặt lịch": khách mới `Trần Thị Hoa`, SĐT `0905111222`, dịch vụ trị liệu 60 phút, giờ `15:00`.
**Đúng khi:** lịch hiện ô 15:00–16:00 của Lan; đặt thêm khách khác cũng 15:00 → báo "kín"; chọn 16:00 → được.

### 8.2 Bot tự chốt qua chat
📱 ✎ → gửi:
```
chị muốn đặt lịch trị liệu cổ vai gáy 60 phút 10h sáng mai
```
**Đúng khi:** bot nhắn "để em kiểm tra lịch" rồi **"đã giữ lịch cho chị: Thứ … lúc 10:00, Trị liệu cổ vai gáy…"** (câu này do hệ thống soạn). 💻 Lịch hẹn ngày mai có ô 10:00, nguồn "Bot".

### 8.3 Kín giờ → gợi ý giờ khác
📱 ✎ (khách mới) → gửi `đặt lịch trị liệu cổ vai gáy 60 phút 10h sáng mai`.
**Đúng khi:** bot báo 10:00 kín và đưa 2–3 giờ gần nhất để chọn; không tạo lịch trùng.

### 8.4 Hỏi giờ trống
📱 gửi `ngày mai còn giờ nào trống trị liệu cổ vai gáy` → bot liệt kê các giờ còn trống (không có 10:00).

### 8.5 Dời & hủy qua chat
📱 (cuộc chat 8.2) gửi `chị muốn dời lịch sang 14h` → bot báo đã dời. Gửi `hủy lịch giúp chị` → bot báo đã hủy; 💻 lịch chuyển trạng thái Đã hủy.
Siết chính sách: Cài đặt → Lịch & bán hàng → "Hủy/dời trước" = `100` giờ → đặt lại lịch → `hủy lịch` → bot từ chối ("báo trước 100 giờ") và chuyển nhân viên. (Xong đặt lại 2.)

### 8.6 Nhóm đông → nhân viên
📱 ✎ → `đặt lịch trị liệu cổ vai gáy 60 phút 16h mai cho 3 người` → bot không tự chốt, chuyển nhân viên.

### 8.7 Nhắc lịch & xác nhận
💻 Cài đặt → Lịch & bán hàng: nhắc lần 2 = `2` giờ. Đặt tay cho khách CHAT THỬ (chọn khách từ danh sách) một lịch **sau 1,5 giờ** nữa. Đợi ≤ 1 phút.
**Đúng khi:** 📱 nhận tin "nhắc lịch…"; gửi `ok` → bot "đã ghi nhận … xác nhận lịch"; 💻 lịch thành Đã xác nhận.

### 8.8 Check-in, tóm tắt khách, giường
💻 mở lịch của 8.2 (hoặc lịch bot khác) → thấy **Tóm tắt cho kỹ thuật viên** (AI viết); bấm Đã đến → Đang làm → Hoàn thành.
Cài đặt → Chi nhánh → Giường/phòng: thêm 1 giường → đặt 2 lịch cùng giờ với 2 nhân viên khác nhau → lịch thứ 2 báo kín; thêm giường 2 → được.

---

## Mẫu báo kết quả (chép, điền, gửi Claude)
```
BUỔI …
… đạt / chưa:
… đạt / chưa:
Ghi chú (mục chưa đạt: bot nói gì + ảnh chụp):
```
