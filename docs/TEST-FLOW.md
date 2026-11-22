# Kịch bản test chi tiết (2026-10-15)

> **Bản đầy đủ, có ô Đạt/Lỗi:** `docs/test-guide/remi-test-guide.html` (mở bằng trình duyệt; nguồn `docs/test-guide/guide.json`, 185 bước, cập nhật 2026-11-17) và bản online artifact "Kịch bản test Remi" (ghim ở thanh bên claude.ai). Tài liệu này giữ chi tiết từng buổi.

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

**Trước khi bắt đầu:** 💻 Thiết lập trợ lý → Giọng văn & xưng hô (hoặc tab Giọng văn trong Chat thử) → chọn **"Như nhân viên thật"** → Lưu.

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
💻 (tin cuối đang là tin khách ở 2.3, chưa ai trả lời) bấm **"Trả lại cho bot"**.
**Đúng khi:** khách KHÔNG cần nhắn lại — trong vài giây bot tự trả lời tin "gói giảm mỡ bụng" ở 2.3: **10 buổi, 120 phút/buổi, 13.000.000đ**.
📱 gửi tiếp:
```
gói giảm mỡ bụng bao nhiêu buổi vậy em
```
**Đúng khi:** bot trả lời bình thường. (Nhân viên đã trả lời rồi mới bấm "Trả lại cho bot" → bot không nhắn gì thêm.)

### 2.5 Nhắc khi nhân viên bỏ quên
Nên làm **sau 2.7** (đã kết nối Telegram) — lời nhắc chủ yếu gửi qua Telegram.
💻 Cài đặt → **Thông báo** → "Nhắc lại khi khách chờ quá" = **5 phút** (nhỏ nhất) → Lưu.
📱 ✎ → gửi `cho chị gặp nhân viên` → 💻 bấm Tiếp quản nhưng **không trả lời**; 📱 gửi thêm `alo em ơi`.
**Đúng khi:** Hộp thư: cuộc chat in đậm, có dòng "Khách đang chờ nhân viên · N phút" đếm lên; sau 5–6 phút nhóm Telegram nhận "[Chat thử] ⏰ Khách đã chờ quá 5 phút chưa ai trả lời". Chat thử chỉ nhắc **1 lần**; khách Zalo thật thì cứ mỗi 5 phút nhắc lại tới khi nhân viên trả lời.

### 2.6 Câu chuyển tiếp + giờ làm việc
💻 **Thiết lập trợ lý → Chuyển nhân viên**:
- Sửa câu chuyển tiếp (thêm chữ dễ nhận ra, vd "— test 2.6") → Lưu → 📱 ✎ → `cho chị gặp nhân viên` → câu bot dùng đúng câu đã sửa.
- Đặt giờ làm việc **kết thúc trước giờ hiện tại** (vd 08:00–09:00) → Lưu → 📱 ✎ → `cho chị gặp nhân viên` → bot dùng câu **ngoài giờ**.
- Xong nhớ **đặt lại giờ làm việc đúng**.

### 2.7 Telegram
💻 Cài đặt → **Thông báo** → Kết nối Telegram → lấy mã kết nối → trong nhóm Telegram có bot, gõ (thay `<bot>` và `MÃ`):
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
💻 **Thiết lập trợ lý → Chăm sóc chủ động** → **"Ai nhắn khách?"** → **Chỉ gợi ý** → Lưu → làm lại 3.3 với khách mới.
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
💻 Tổng quan. **Đúng khi:** thấy lời chào + 3 ô "Cần xử lý ngay" (khách chờ nhân viên, khách cần chăm sóc, hóa đơn còn nợ), khối "Hôm nay" (doanh thu, lịch hẹn, khách mới, hóa đơn + lịch sắp tới), thẻ tím "Trợ lý AI · 30 ngày qua", thẻ "Thiết lập hệ thống" (ẩn khi xong hết), và phần "Hoạt động của bot" (biểu đồ 7/30 ngày, câu bot chưa trả lời được). Chat thử không tính vào số liệu bot — bình thường.

### 5.2 Link chính sách
💻 Cài đặt → **Doanh nghiệp** → link chính sách bảo mật:
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
💻 (chủ DN) Cài đặt → **Tài khoản & phân quyền** → thêm:
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
2. 💻 **Kênh Zalo** → **Kết nối Zalo OA** → trạng thái "Đang hoạt động".
3. Zalo cá nhân nhắn OA → làm lại 1.1–1.10 với đúng các câu ở trên → bot trả lời trên Zalo trong vài giây.
4. Nhân viên trả lời từ Hộp thư → khách nhận trên Zalo; nhân viên trả lời trong app OA → bot dừng.
5. Ngắt kết nối → nhắn OA → bot không trả lời.

---

## Buổi 7 — CRM C1: chi nhánh, danh mục, nhân viên, quyền (≈ 25 phút)

### 7.1 Chi nhánh mặc định
💻 Cài đặt → **Chi nhánh & giờ mở cửa**.
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
💻 Cài đặt → **Tài khoản & phân quyền** → với `nhanvien1` bấm **Quyền** → bật `Quản lý danh mục bán`, chi nhánh `Q12` → Lưu. Tab ẩn danh đăng nhập nhân viên.
**Đúng khi:** nhân viên sửa được giá ở Danh mục; ở Cài đặt → Chi nhánh & giờ mở cửa chỉ thấy `Q12` và không có nút sửa; trang Nhân viên không có nút thêm/sửa. Chủ tắt quyền → nhân viên tải lại trang mất nút sửa Danh mục.

## Buổi 8 — CRM C2: lịch hẹn & bot tự chốt (≈ 30 phút)
Chuẩn bị: Nhân viên Lan có ca cố định T2–CN `08:00–20:00` và làm được dịch vụ "Trị liệu cổ vai gáy / lưng eo 60 phút" (hoặc mọi dịch vụ). Cài đặt → **Lịch hẹn & bán hàng**: "Bot tự chốt lịch" đang bật.

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
Siết chính sách: Cài đặt → **Lịch hẹn & bán hàng** → "Hủy/dời trước" = `100` giờ → đặt lại lịch → `hủy lịch` → bot từ chối ("báo trước 100 giờ") và chuyển nhân viên. (Xong đặt lại 2.)

### 8.6 Nhóm đông → nhân viên
📱 ✎ → `đặt lịch trị liệu cổ vai gáy 60 phút 16h mai cho 3 người` → bot không tự chốt, chuyển nhân viên.

### 8.7 Nhắc lịch & xác nhận
💻 Cài đặt → **Lịch hẹn & bán hàng**: nhắc lần 2 = `2` giờ. Đặt tay cho khách CHAT THỬ (chọn khách từ danh sách) một lịch **sau 1,5 giờ** nữa. Đợi ≤ 1 phút.
**Đúng khi:** 📱 nhận tin "nhắc lịch…"; gửi `ok` → bot "đã ghi nhận … xác nhận lịch"; 💻 lịch thành Đã xác nhận.

### 8.8 Check-in, tóm tắt khách, giường
💻 mở lịch của 8.2 (hoặc lịch bot khác) → thấy **Tóm tắt cho kỹ thuật viên** (AI viết); bấm Đã đến → Đang làm → Hoàn thành.
Cài đặt → Chi nhánh & giờ mở cửa → mở chi nhánh → Giường/phòng: thêm 1 giường → đặt 2 lịch cùng giờ với 2 nhân viên khác nhau → lịch thứ 2 báo kín; thêm giường 2 → được.

## Buổi 9 — CRM C3: bán hàng, gói, ví, VietQR (≈ 30 phút)
Chuẩn bị: Cài đặt → Thanh toán: thêm tài khoản ngân hàng thật của bạn (BIN 6 số, số tài khoản, tên chủ TK). Cài đặt → **Lịch hẹn & bán hàng**: ngưỡng giảm giá cần duyệt `20`%.

### 9.1 Hóa đơn + giảm giá + thu 2 lần
💻 **Bán hàng** → Tạo hóa đơn: khách `Trần Thị Hoa`, dòng "Trị liệu cổ vai gáy 60 phút" + "Tinh dầu tràm 50ml" x2 → giảm giá `50000` lý do `khách quen` → Thu tiền mặt `300000` → Thu chuyển khoản phần còn lại.
**Đúng khi:** số hóa đơn `HD-000001`; sau lần thu 1 trạng thái "Thu một phần"; thu đủ → "Đã thanh toán"; không sửa dòng được nữa.

### 9.2 VietQR
Ở hóa đơn còn nợ → bấm **Mã QR chuyển khoản** → **quét bằng app ngân hàng của bạn**.
**Đúng khi:** app hiện đúng ngân hàng, số tài khoản, số tiền còn nợ, nội dung `HD000002`. (Chưa chuyển thật; nếu app báo mã không hợp lệ → chụp màn hình gửi Claude.)

### 9.3 Voucher
Bán hàng → tab **Voucher**: tạo `GIAM10`, 10%, tối đa 100.000. Hóa đơn mới → nhập mã `giam10` → giảm đúng 10%; nhập `SAI` → báo mã không tồn tại.

### 9.4 Gói trừ buổi
Hóa đơn bán "Gói … 10 buổi" cho Hoa → thu đủ. Hồ sơ khách Hoa → mục **Gói** hiện "còn 10/10". Lịch hẹn của Hoa (dịch vụ thuộc gói) → **Thanh toán** → hóa đơn hiện dòng "Dùng buổi gói", tổng 0 → **Chốt (0 đ)** → gói còn 9/10.
Hủy hóa đơn dùng buổi (chủ) → gói về 10/10.

### 9.5 Hoàn thành lịch tự trừ buổi
Lịch khác của Hoa (dịch vụ trong gói) → bấm Hoàn thành mà KHÔNG tạo hóa đơn → gói giảm 1 buổi; mở hóa đơn từ lịch đó → không trừ thêm.

### 9.6 Ví trả trước
Hồ sơ Hoa → Ví → Nạp `1000000` thưởng `100000` → số dư 1.100.000. Hóa đơn mới → thu bằng **Ví** → số dư giảm đúng; thu quá số dư → báo lỗi.

### 9.7 Quyền nhân viên
Tab ẩn danh nhân viên: tạo hóa đơn → giảm 30% → bị chặn (cần quyền); giảm 10% → được; bấm Hủy hóa đơn → không có quyền. Chủ bật quyền "Hủy hóa đơn" → nhân viên hủy được (phải ghi lý do).

### 9.8 Ca thu ngân
Bán hàng → **Mở ca** tiền đầu ca `500000` → thu vài hóa đơn tiền mặt → **Chốt ca** nhập tiền đếm → thấy chênh lệch đúng.

### 9.9 Phiếu thu qua chat
Hóa đơn của khách CHAT THỬ (đã nhắn trước đó) → **Gửi phiếu thu** → 📱 nhận tin "PHIẾU THU HD-…" có tổng và hình thức thanh toán.

### 9.10 Hoàn tiền
Hóa đơn đã thu → **Hoàn tiền** `100000` tiền mặt lý do `khách không hài lòng` → trạng thái "Đã hoàn"; báo cáo doanh thu (C6) trừ đúng.

## Buổi 10 — CRM C4: kho hàng (≈ 20 phút)
Chuẩn bị: Danh mục → Sản phẩm: "Tinh dầu tràm 50ml" bật **Theo dõi tồn kho**; thêm sản phẩm `Dầu massage 1L` (đơn vị chai, giá 300.000, bật theo dõi).

### 10.1 Nhập hàng + giá vốn
💻 **Kho** → Nhập hàng: 10 chai tinh dầu @100.000 → Nhận; nhập tiếp 10 chai @120.000 → Nhận.
**Đúng khi:** tồn 20, giá vốn bình quân 110.000.

### 10.2 Bán trừ kho, hủy trả kho
Bán hàng → hóa đơn 3 chai tinh dầu → thu đủ → Kho hàng: tồn 17. Hủy hóa đơn đó → tồn 20. Lịch sử kho có dòng "Bán" và "Trả lại".

### 10.3 Định mức vật tư
Kho hàng → Định mức → dịch vụ "Trị liệu cổ vai gáy 60 phút" dùng `0.05` chai Dầu massage 1L. Nhập 2 chai dầu. Lịch hẹn → Hoàn thành một lịch trị liệu → Kho: dầu còn 1.95.

### 10.4 Chuyển kho, kiểm kê, cảnh báo
Chuyển 5 chai tinh dầu sang chi nhánh Q12 → Q12 tồn 5, CN1 tồn 15. Kiểm kê Q12 đếm 4 → tồn 4, có dòng điều chỉnh −1. Đặt mức tối thiểu Q12 = 5 → bộ lọc "Sắp hết" hiện tinh dầu Q12.

### 10.5 Quyền
Nhân viên không có "Quản lý kho": xem được tồn, không nhập/điều chỉnh được.

## Buổi 11 — CRM C5: hoa hồng, chấm công, hiệu suất (≈ 20 phút)
Chuẩn bị: Nhân viên → Lan gắn tài khoản `nhanvien1@khoahochuyetdao.local`.

### 11.1 Quy tắc hoa hồng
💻 Nhân viên → tab **Hoa hồng** → thêm quy tắc chung: Dịch vụ 10%; quy tắc riêng Lan: Dịch vụ 20%; Gói: 50.000/buổi.

### 11.2 Phát sinh & thu hồi
Bán hàng → hóa đơn trị liệu 550.000, người thực hiện Lan → thu đủ → Hoa hồng tháng này của Lan = 110.000. Hóa đơn khác, người thực hiện Mai → 55.000. Hủy hóa đơn của Mai → bảng hoa hồng Mai về 0.

### 11.3 Gói theo buổi
Hóa đơn "Dùng buổi gói" cho khách có gói, người thực hiện Lan → Chốt (0 đ) → Lan +50.000.

### 11.4 Chấm công
Tab ẩn danh nhân viên (Lan) → góc trên "Vào ca" → bấm lần 2 báo đang trong ca → "Ra ca". Chủ: Chấm công → ghi hộ cho Mai, sửa giờ.

### 11.5 Hiệu suất & quyền
Chủ: Nhân viên → tab **Hiệu suất** (khoảng 30 ngày): Lan có lịch hoàn thành, doanh thu, hoa hồng, giờ làm. Nhân viên Lan chỉ thấy dòng của mình; đánh dấu "Đã trả" (chủ) → cột Đã trả cập nhật.

## Buổi 12 — CRM C6: báo cáo (≈ 15 phút)
Làm sau Buổi 9–11 (đã có hóa đơn, lịch).

### 12.1 Hôm nay
💻 **Báo cáo** → Hôm nay: số lịch / hoàn thành / không đến, doanh thu, tiền mặt, chuyển khoản, hóa đơn còn nợ khớp với những gì đã làm trong ngày.

### 12.2 Doanh thu
Tab Doanh thu, 30 ngày, theo ngày → tổng khớp tổng các hóa đơn đã thu (trừ hoàn). Đổi "theo tuần" / "theo tháng". Xem theo dịch vụ, nhân viên, nguồn khách ("Bot Zalo chốt" nếu có lịch bot), hình thức thanh toán. Bấm **Xuất Excel** → file mở được, sheet "Theo dịch vụ" đúng.

### 12.3 Hiệu quả AI, Lấp đầy, Khách, Công nợ
Mỗi tab mở không lỗi; Công nợ liệt kê hóa đơn còn nợ (Buổi 9.2) và buổi gói chưa dùng.

### 12.4 Hỏi bằng tiếng Việt
Ô "Hỏi số liệu": `Tháng này dịch vụ nào bán chạy nhất?` → câu trả lời nêu đúng dịch vụ và số tiền như tab Doanh thu. Hỏi `Thời tiết hôm nay thế nào?` → báo không thuộc báo cáo.

### 12.5 Báo cáo tuần
Nếu đã kết nối Telegram: Cài đặt → Thông báo → bấm "Gửi thử báo cáo tuần" (nếu có) hoặc đợi 08:00 thứ 2 → nhóm Telegram nhận tóm tắt.

---

## Buổi 13 — Zalo cá nhân, chạy giả lập (≈ 40 phút)

Chưa cần nick thật: sidecar chạy **giả lập** (cửa sổ "ZaloAi Personal :4100"). Khung **"Giả lập Zalo cá nhân"** ở cuối thẻ nick (trang Kênh Zalo) đóng vai khách; câu trả lời của bot xem ở **Hộp thư**.
Bot qua Zalo cá nhân trả lời **chậm hơn chat thử: 15–30 giây** (gom tin 8 giây + giả gõ 3–12 giây, cho giống người thật). Việc bot tự đi nhắn (dán SĐT) chạy **mỗi phút một lượt**.

### 13.0 Chuẩn bị
1. `.\stop` rồi `.\dev` → phải đủ 4 cửa sổ **API, Worker, Portal, ZaloAi Personal :4100**.
2. 💻 Đăng nhập `owner@khoahochuyetdao.local` / `Dev@123456` (DN có kho kiến thức nên bot trả lời có nội dung).
3. Mở 2 tab: **Tab A** = Kênh Zalo (`/channels`), **Tab B** = Hộp thư (`/inbox`).
4. Test trong **8:00–20:00** (ngoài giờ bot không tự đi nhắn).

### 13.1 Kết nối nick bằng QR
Tab A → mục **Zalo cá nhân** → **Kết nối nick** → tích ô rủi ro → tiếp tục.
- Bấm **Giả lập từ chối** → **Đúng khi:** báo bị từ chối, có nút thử lại.
- Thử lại → **Giả lập hết hạn** → **Đúng khi:** báo mã hết hạn.
- Thử lại → **Giả lập quét** → **Đúng khi:** báo kết nối xong; thẻ "Nick thử" **Đang chạy**; thẻ *Chế độ an toàn* hiện 0/3 lời mời, 0/20 người mới, "IP riêng: chưa có" (dev không bắt buộc proxy).

### 13.2 Khách lạ nhắn → bot trả lời
Tab A → mở **Giả lập Zalo cá nhân** → Mã Zalo (uid) `khach-1`, Tên `Chị Mai`, **bỏ tích** "Là bạn bè", nội dung:
```
trị liệu cổ vai gáy 60 phút giá bao nhiêu
```
**Đúng khi:** Tab B có hội thoại Chị Mai ghi tên nick; 15–30 giây sau bot trả lời, tin đầu báo là trợ lý AI, giá **550.000đ/lượt**.
Gửi lại có tích **Nhắn trong nhóm** → **Đúng khi:** Hộp thư không có gì mới (bot không đọc nhóm).

### 13.3 Chủ nick nhắn tay → bot tạm im
Tab A, uid vẫn `khach-1`, ô **Chủ nick nhắn tay**: `Dạ chị đợi em xíu nhé` → Gửi.
- **Đúng khi:** Tab B hiện tin đó và dải "Bot đang tạm im đến HH:mm vì chủ nick vừa nhắn tay" (30 phút).
- Khách (`khach-1`) nhắn `còn slot chiều nay không em` → **Đúng khi:** bot **không** trả lời.
- Bấm **Bật lại bot** → khách nhắn tiếp → **Đúng khi:** bot trả lời.

### 13.4 Bạn bè nhắn → bot không đọc
uid `ban-1`, tên `Anh Tuấn`, **tích "Là bạn bè"**, nội dung bất kỳ → Gửi.
- **Đúng khi:** Hộp thư không có hội thoại Anh Tuấn; nút **Người quen nhắn** trên thẻ nick có số 1.
- Mở **Người quen nhắn** → **Đúng khi:** thấy Anh Tuấn, *không* thấy nội dung tin. Bấm **Đây là khách** → Anh Tuấn nhắn tiếp → bot trả lời. (Bấm **Riêng tư** thì bot không bao giờ đọc.)

### 13.5 Dán SĐT → bot tự nhắn chào
Menu **Chăm sóc Zalo cá nhân** → tab **Thêm SĐT** → dán:
```
0911222333, chị Lan
0911222000, anh Bình
0911222333
abc
```
Nguồn **Khách giới thiệu người quen**, Người giới thiệu tên `Chị Hoa`, tích ô xác nhận → **Bắt đầu chăm sóc**.
- **Đúng khi:** nhận 2, bỏ qua 2 (1 trùng, 1 sai định dạng), có số ngày dự kiến.
- Đợi 1–2 phút → tab **Đang chăm sóc**: chị Lan **Đã nhắn, chờ trả lời**; anh Bình **Không có Zalo – gọi điện** (giả lập: số đuôi `000` = không có Zalo).
- Tab B, hội thoại chị Lan: tin chào báo trợ lý AI, nhắc "Chị Hoa có giới thiệu chị Lan…", chỉ hỏi xin phép, **không có giá**.
- Thẻ Chế độ an toàn: **Người mới nhắn lần đầu 1/20**.

### 13.6 Khách trả lời → bot tư vấn
Tab A giả lập: uid `fake-0911222333` (giả lập đặt uid = `fake-` + SĐT), tên `chị Lan`, nội dung `ừ em gửi chị xem với` → Gửi.
**Đúng khi:** Đang chăm sóc → chị Lan **Đang tư vấn**; Tab B bot tư vấn theo kho kiến thức (15–30 giây).

### 13.7 Khách nhắn "hủy" → không bao giờ nhắn lại
uid `fake-0911222333` nhắn `hủy`. Rồi dán lại `0911222333` ở Thêm SĐT.
**Đúng khi:** bị bỏ qua với lý do đã từ chối nhận tin. Trong vòng 1 giờ (job dọn chạy phút thứ 20 mỗi giờ) chị Lan chuyển **Từ chối**.

### 13.8 (Tùy chọn) Zalo từ chối tin → mời kết bạn, lỗi thì dừng hẳn
Dán `0911222444, [[blocked]]` (tên `[[blocked]]` làm giả lập từ chối mọi tin có chữ đó).
**Đúng khi:** lượt đầu → **Đã mời kết bạn** (Lời mời kết bạn hôm nay 1/3) → giả lập tự chấp nhận → **Đã kết bạn** → lượt sau tin vẫn bị từ chối → **Lỗi**, và đứng yên ở Lỗi (Hộp thư chỉ có 2 tin lỗi, không tăng thêm mỗi phút).

### 13.9 Nhịp gửi
Tab A → **Nhịp gửi**. **Đúng khi:** An toàn hiện 3 / 20 / 200 và giải thích; chọn **Tùy chỉnh** → mỗi ô có khuyến cáo, không ô nào vượt trần; lưu số cao hơn mức an toàn phải tích "Tôi hiểu và tự chịu rủi ro…". Lưu xong thẻ hiện "Nhịp gửi: Tùy chỉnh". Đổi lại **An toàn**.

### 13.10 Mất kết nối
Trong khung giả lập:
- **Rớt mạng** → **Đúng khi:** vài giây sau tự **Đang chạy** lại (chưa đổi thì tải lại trang).
- **Mở Zalo Web nơi khác** → **Đúng khi:** thẻ báo "Bị ngắt vì mở Zalo Web", bot ngừng trả lời nick này → bấm **Kết nối lại** (không cần QR) → Đang chạy.
- **Phiên hết hạn** → **Đúng khi:** thẻ báo "Cần quét lại mã QR" → **Quét lại** → **Giả lập quét** → **chính nick cũ** chạy lại (không thêm nick thứ hai, cài đặt và người quen còn nguyên).
- **Proxy hỏng** → **Đúng khi:** "Đường kết nối riêng đang lỗi", bot tạm dừng ở nick này → **Kết nối lại**.
- Có cài Telegram thì nhóm nhận báo nick mất kết nối.

### 13.11 Tin nhắn mẫu
Cài đặt → **Tin nhắn mẫu** → **Thêm mẫu**: Tên `Bảng giá cổ vai gáy`, Phím tắt `gia`, Nội dung bất kỳ → Lưu.
Hộp thư → hội thoại Chị Mai → **Tiếp quản** → ô trả lời gõ `/gia` → **Đúng khi:** hiện danh sách mẫu, Enter chèn nội dung, gửi được.

### 13.12 Quyền + super admin
- Đăng nhập `nhanvien1@khoahochuyetdao.local` (nếu đã tạo ở 5b) → **Đúng khi:** vào được Chăm sóc Zalo cá nhân và dán SĐT được; ở Kênh Zalo chỉ xem, không có nút sửa.
- Đăng nhập `admin@zaloai.local` → **Proxy Zalo cá nhân** → **Đúng khi:** thấy "Nick thử" của Khoa Học Huyệt Đạo, chưa có IP riêng. (Không cần thêm proxy ở dev.)

---

## Buổi 14 — Zalo cá nhân với nick THẬT (≈ 45 phút)

Không cần tunnel/tên miền như OA: sidecar tự kết nối ra Zalo. Dev không bắt buộc proxy (buổi này dùng mạng nhà; proxy ở 14.9).

### 14.0 Chuẩn bị
- **Nick phụ**: SIM trả trước + điện thoại cũ, đã dùng bình thường ~1 tuần (ảnh, tên, vài bạn bè, vài tin). **Không bao giờ** kết nối nick chính vào Remi.
- **Nick chính** của bạn đóng vai khách. Trong Zalo nick chính: Cài đặt → Quyền riêng tư → bật cho người khác **tìm thấy bằng số điện thoại** (để bot tìm được).
- Test trong **8:00–20:00**. Chỉ dán số của chính bạn hoặc người đã đồng ý.

### 14.1 Gỡ nick giả lập
Đăng nhập `owner@anphat.local` và `owner@khoahochuyetdao.local` → Kênh Zalo → **Ngắt kết nối** "Nick thử…" ở cả hai.
**Vì sao:** để chế độ thật không đem phiên giả gửi lên Zalo; mỗi DN tối đa 2 nick.

### 14.2 Chuyển sang chế độ thật
1. Sửa `connectors\zalo-personal\.env`: `REMI_MODE=real`
2. PowerShell ở `zalo-ai-assistant` (API và Worker dùng chung kho bí mật):
   ```
   dotnet user-secrets set ZaloPersonal:Mode Real --project src/ZaloAi.Api
   ```
3. `.\stop` rồi `.\dev`. **Đúng khi:** trang Kênh Zalo không còn khung "Giả lập".

### 14.3 Kết nối nick phụ
`owner@khoahochuyetdao.local` → Kênh Zalo → **Kết nối nick** → tích ô rủi ro → mã QR thật.
Điện thoại nick phụ: Zalo → biểu tượng **quét QR** (cạnh ô tìm kiếm) → quét → **xác nhận đăng nhập**.
**Đúng khi:** vài giây sau thẻ nick **Đang chạy**, đúng tên nick phụ. Không lên → chụp cửa sổ "ZaloAi Personal :4100".

### 14.4 Bot tự tìm và chào (làm TRƯỚC khi nick chính nhắn)
Chăm sóc Zalo cá nhân → Thêm SĐT → dán `<SĐT nick chính>, <tên bạn>` → nguồn **Khách cũ đã dùng dịch vụ** → tích xác nhận → Bắt đầu.
**Đúng khi (từ 2026-11-15 — kết bạn trước khi còn lượt mời):** trong 1–2 phút nick chính nhận **lời mời kết bạn kèm lời chào**
(báo trợ lý AI, không giá) → trạng thái **Đã mời kết bạn**, chưa có tin nhắn nào.
- Chấp nhận lời mời → trong 1–2 phút nhận tin: cảm ơn + báo trợ lý AI + nói sơ bên mình có các liệu trình gì + hỏi xin phép → **Đã nhắn, chờ trả lời**.
- Lời chào do AI viết riêng (mỗi người một kiểu, không văn mẫu). Muốn thử luồng "nhắn trước": Kênh Zalo → ⋯ → Nhịp gửi → Tùy chỉnh, lời mời/ngày = 1 (lượt duy nhất giữ cho người đã trả lời).
- Báo **Không có Zalo** → xem lại quyền "tìm bằng số điện thoại" ở 14.0.
(Nick chính nhắn trước thì số này bị bỏ qua vì "đang chat".)

### 14.5 Khách trả lời → bot tư vấn
Nick chính trả lời `ừ gửi mình xem`, rồi `trị liệu cổ vai gáy 60 phút giá bao nhiêu`.
**Đúng khi:** 15–30 giây bot trả lời trên Zalo nick chính, giá 550.000đ; Đang chăm sóc → **Đang tư vấn**; Hộp thư có hội thoại ghi tên nick.

### 14.6 Chủ nick nhắn tay + nhân viên trả lời
- Trên điện thoại nick phụ, nhắn tay cho nick chính → **Đúng khi:** Hộp thư hiện tin đó + "Bot đang tạm im"; nick chính nhắn tiếp → bot im; **Bật lại bot** → bot trả lời.
- Hộp thư → **Tiếp quản** → gõ trả lời (thử `/` chèn mẫu) → **Đúng khi:** nick chính nhận; **Trả lại cho bot**.

### 14.7 Mất kết nối
- Mở https://chat.zalo.me bằng nick phụ trên trình duyệt → **Đúng khi:** Remi báo "Bị ngắt vì mở Zalo Web". Đăng xuất Zalo Web → **Kết nối lại** → Đang chạy.
- `.\stop` rồi `.\dev` → **Đúng khi:** nick tự chạy lại, không cần quét QR.

### 14.8 Ghi lại gửi Claude (để ghi vào zalo-api-notes)
Quét QR tới Đang chạy mất bao lâu; bot trả lời mất bao lâu; tìm SĐT có ra không; lời mời có hiện đủ lời chào không; mở Zalo PC trên máy tính có làm rớt nick không; điện thoại nick phụ có hiện cảnh báo gì của Zalo không.
**Không** nhắn `hủy` từ nick chính (khách nhắn hủy thì không bật lại được) — để test hủy cuối cùng nếu muốn.

### 14.9 (Buổi sau) Proxy
Mua 1 proxy dân cư/4G Việt Nam IP cố định → `admin@zaloai.local` → **Proxy Zalo cá nhân** → Thêm proxy → gán cho nick phụ (nick đăng nhập lại qua IP mới; Zalo đòi xác minh thì quét QR lại).
**Đúng khi:** IP ra trên trang đúng IP proxy; nhận/gửi tin bình thường; proxy hỏng → nick dừng, không tự đi thẳng mạng nhà.

### 14.10 Quay về giả lập
`REMI_MODE=fake` trong `.env`, chạy `dotnet user-secrets remove ZaloPersonal:Mode --project src/ZaloAi.Api`, ngắt nick thật nếu muốn, `.\stop` + `.\dev`.

---

## Mẫu báo kết quả (chép, điền, gửi Claude)
```
BUỔI …
… đạt / chưa:
… đạt / chưa:
Ghi chú (mục chưa đạt: bot nói gì + ảnh chụp):
```
