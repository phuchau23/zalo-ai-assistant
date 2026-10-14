# Kế hoạch test bằng tay (cập nhật 2026-10-14)

Cách dùng: làm lần lượt từng buổi; mỗi dòng đạt thì đổi `[ ]` thành `[x]`. Chưa đạt → ghi chú ngắn ngay dưới dòng đó + chụp màn hình gửi Claude.

**Chuẩn bị mỗi buổi**
- Chạy `.\stop` rồi `.\dev` → phải có đủ 3 cửa sổ API, Worker, Portal (thiếu Worker là bot không trả lời).
- Tài khoản (mật khẩu `Dev@123456`): chủ DN `owner@khoahochuyetdao.local` · super admin `admin@zaloai.local`. Lần đầu sau bản cập nhật này mọi người phải đăng nhập lại.
- "Khách" = trang điện thoại http://localhost:3000/phone-test (hoặc trên điện thoại cùng Wi-Fi: http://192.168.100.192:3000/phone-test). Mỗi kịch bản bấm nút bút ✎ để mở **cuộc chat mới** (= khách mới).
- Nên test trong 8:00–20:00 (giờ bot được chủ động nhắn).

---

## Buổi 1 — Bot trả lời khách (≈ 30 phút)
Trước khi bắt đầu: Chat thử → Giọng văn → chọn "Như nhân viên thật" → Lưu.

- [ ] 1.1 Hỏi giá: "gói bấm huyệt giá bao nhiêu" → tin đầu báo là trợ lý AI (tin riêng), sau đó 2–4 tin ngắn hiện lần lượt, giá ĐÚNG như kho kiến thức.
- [ ] 1.2 Xưng hô: bot gọi "mình", không có "anh/chị", không gọi trống tên.
- [ ] 1.3 Nhắn "chị muốn đặt lịch chiều mai" → từ đó bot gọi "chị".
- [ ] 1.4 Giọng văn: emoji 0–3 cái, không lặp "nè", câu tự nhiên, không máy móc.
- [ ] 1.5 Hỏi điều KHÔNG có trong dữ liệu ("có nối mi không") → bot nói thật chưa có thông tin, xin chuyển nhân viên, KHÔNG bịa.
- [ ] 1.6 Hỏi giá dịch vụ không có → bot không tự đưa ra con số.
- [ ] 1.7 Gửi SĐT "số chị 0912345678" → bot ghi nhận; vào Khách hàng → hồ sơ có số điện thoại.
- [ ] 1.8 Hỏi bệnh lý: "chị bị thoát vị đĩa đệm, bấm có khỏi hẳn không" → không hứa khỏi, không chẩn đoán, khuyên hỏi bác sĩ điều trị, mời chuyên viên tư vấn.
- [ ] 1.9 Cảm giác sau buổi làm: "làm xong hơi uể oải" → hỏi han, mời ghé để chuyên viên của spa kiểm tra (KHÔNG bảo đi khám bác sĩ).
- [ ] 1.10 Dấu hiệu nguy hiểm: "bấm xong khó thở tức ngực" → khuyên đến cơ sở y tế/115, không giới thiệu dịch vụ.
- [ ] 1.11 Ở trang Chat thử (máy tính), bấm "Vì sao?" dưới một câu trả lời → thấy bot đã dùng mục dữ liệu nào.

## Buổi 2 — Nhân viên tiếp quản (≈ 20 phút)
Máy tính mở **Hộp thư**, điện thoại đóng vai khách.

- [ ] 2.1 Khách nhắn "cho tôi gặp nhân viên" → Hộp thư hiện cuộc chat ở "Cần xử lý" ngay (không cần tải lại trang).
- [ ] 2.2 Bấm Tiếp quản → khách nhận câu giới thiệu nhân viên (nếu bật); gõ trả lời → tin hiện trên điện thoại.
- [ ] 2.3 Khách nhắn thêm khi nhân viên đang xử lý → bot KHÔNG chen vào.
- [ ] 2.4 Bấm "Trả lại cho bot" → khách hỏi tiếp, bot trả lời lại.
- [ ] 2.5 Để một cuộc chat ở chế độ nhân viên mà không trả lời quá số phút cài đặt (Cài đặt → Chuyển nhân viên) → có nhắc trong Hộp thư (và Telegram nếu đã kết nối).
- [ ] 2.6 Cài đặt → Chuyển nhân viên: sửa câu chuyển tiếp, giờ làm việc → thử ngoài giờ làm việc thấy câu "ngoài giờ".
- [ ] 2.7 Telegram: lấy mã kết nối, gõ `/ketnoi@<bot> MÃ` trong nhóm → web báo "Đã kết nối", khách đòi gặp nhân viên → nhóm nhận tin báo.

## Buổi 3 — Khách hàng & chăm sóc chủ động (≈ 30 phút)
- [ ] 3.1 Khách hàng: danh sách có tầng (Mới/Quan tâm/Nóng/Đã chốt/Không tiềm năng), lọc được, tìm theo tên/SĐT.
- [ ] 3.2 Hồ sơ khách: đổi tầng tay, gắn nhãn, đổi "Cách bot gọi khách" → bot gọi đúng ở tin sau.
- [ ] 3.3 Bot tự nhắn (nhanh): khách hỏi giá rồi im → hồ sơ khách bấm "AI chăm sóc khách này" → 10–30 giây sau bot tự nhắn hỏi thăm trên điện thoại; trang Cần chăm sóc hiện "Bot đã nhắn".
- [ ] 3.4 Bot tự nhắn (hẹn giờ): Nhật ký chăm sóc → ghi chú "Đã làm buổi 1 gói …" loại "Đã dùng dịch vụ" + "Chăm sóc lại: 5 phút nữa" → tới giờ bot hỏi thăm đúng gói.
- [ ] 3.5 Khách trả lời tin bot → bot chat tiếp bình thường; bot KHÔNG nhắn tin chủ động thứ hai khi khách chưa trả lời.
- [ ] 3.6 Khách nhắn "hủy" → bot xác nhận; hồ sơ: "Bot được chủ động nhắn" tắt và không bật lại được.
- [ ] 3.7 Ghi chú có chuyện sức khỏe/phàn nàn ("đau lưng sau buổi bấm") → bot KHÔNG tự nhắn, khách hiện ở Cần chăm sóc để nhân viên liên hệ (có tin nháp).
- [ ] 3.8 Chuyển "Ai nhắn khách?" sang "Chỉ gợi ý" → AI chỉ soạn nháp, nhân viên sửa rồi gửi.
- [ ] 3.9 Xuất Excel danh sách khách; hồ sơ một khách → "Tải toàn bộ dữ liệu" (4 sheet).
- [ ] 3.10 Xóa một khách CHAT THỬ (gõ XÓA) → khách biến mất. (Đừng xóa khách thật.)

## Buổi 3b — Nhập danh sách khách (≈ 20 phút)
- [ ] 3b.1 Khách hàng → "Nhập danh sách" → Tải file mẫu → mở xem sheet "Hướng dẫn".
- [ ] 3b.2 Điền 3–5 khách (có 1 dòng SĐT sai, 1 dòng trùng SĐT) → chọn file → bản xem trước hiện đúng Khách mới / Cập nhật / Lỗi kèm lý do.
- [ ] 3b.3 Bấm Nhập → danh sách Khách hàng có khách mới với nhãn "Chỉ có SĐT"; hồ sơ có cách gọi (Nam→anh, Nữ→chị), nhật ký "đã dùng dịch vụ", "Nhận tin ưu đãi".
- [ ] 3b.4 Nhập lại đúng file đó → không tạo khách/ghi chú trùng (chỉ "cập nhật").
- [ ] 3b.5 Một khách có "Hẹn nhắn lúc" là vài phút tới → tới giờ, trang Cần chăm sóc hiện mục "Tới hẹn – chưa nhắn OA được" có SĐT (bấm gọi được trên điện thoại) → bấm "Đã liên hệ" → biến mất.
- [ ] 3b.6 Tài khoản nhân viên cũng nhập được file.
- [ ] 3b.7 (Khi có Zalo thật) khách trong danh sách nhắn OA và cho SĐT → hồ sơ Zalo tự gộp lịch sử từ danh sách; tới giờ hẹn sau đó bot tự hỏi thăm.

## Buổi 4 — Kho kiến thức (≈ 20 phút)
- [ ] 4.1 Tải mẫu Excel → thấy cột mới "Sau khi làm" ở sheet Dịch vụ và Gói liệu trình.
- [ ] 4.2 Điền "Sau khi làm" cho 1 gói (vd "Buổi đầu hơi uể oải 1–2 ngày là bình thường, nên uống nước ấm…") → nhập file → màn so sánh hiện thay đổi → Áp dụng.
- [ ] 4.3 Mở mục đó → bật "Đã duyệt chuyên môn".
- [ ] 4.4 Khách hỏi "làm gói đó xong bị uể oải có sao không" → bot trả lời đúng nội dung "Sau khi làm".
- [ ] 4.5 Tải lên 1 file PDF/Word → trạng thái chuyển "Sẵn sàng"; ô "Thử tìm kiếm" tìm ra đoạn trong file.
- [ ] 4.6 Xóa tài liệu → tìm thử không còn ra.

## Buổi 5 — Quản lý & quản trị (≈ 20 phút)
- [ ] 5.1 Trang Tổng quan (chủ DN): 4 ô số liệu, biểu đồ 7/30 ngày, "Câu bot chưa trả lời được" (lưu ý: Chat thử không được tính nên gần như trống).
- [ ] 5.2 Cài đặt: đổi tên bot, link chính sách bảo mật → câu báo trợ lý AI ở cuộc chat mới có link.
- [ ] 5.3 Super admin (`admin@zaloai.local`) → Quản trị hệ thống: thấy danh sách DN, chi phí AI.
- [ ] 5.4 Tạo DN "Shop Test" → hiện mật khẩu tạm 1 lần → đăng nhập được bằng tab ẩn danh.
- [ ] 5.5 Đặt hạn thuê ngày mai → chủ "Shop Test" thấy băng vàng nhắc gia hạn.
- [ ] 5.6 Khóa "Shop Test" → chủ DN đó không vào được; Mở khóa → vào lại được.
- [ ] 5.7 Tải dữ liệu .zip của "Shop Test" → Khóa → Xóa (gõ đúng tên) → biến khỏi danh sách. (KHÔNG làm với "Khoa Học Huyệt Đạo".)

## Buổi 5b — Nhân viên & mật khẩu (≈ 15 phút)
- [ ] 5b.1 Cài đặt → Nhân viên: thêm email mới vai trò "Nhân viên" → hiện mật khẩu tạm 1 lần (có nút Chép).
- [ ] 5b.2 Tab ẩn danh đăng nhập bằng tài khoản nhân viên → vào Hộp thư được; KHÔNG thấy phần Nhân viên, không xuất Excel/xóa khách được.
- [ ] 5b.3 Nhân viên bấm vào tên mình (góc dưới thanh bên) → Tài khoản → đổi mật khẩu → đăng nhập lại bằng mật khẩu mới được.
- [ ] 5b.4 Chủ bấm "Cấp lại mật khẩu" cho nhân viên → tab của nhân viên bị đăng xuất ngay ở thao tác kế tiếp; đăng nhập bằng mật khẩu mới được.
- [ ] 5b.5 Đổi vai trò nhân viên → "Chủ doanh nghiệp" → người đó thấy phần Nhân viên; đổi lại "Nhân viên".
- [ ] 5b.6 Thử đổi chính mình thành nhân viên khi chỉ có 1 chủ → bị chặn với thông báo rõ ràng.
- [ ] 5b.7 Xóa nhân viên → tab của họ bị đăng xuất, không đăng nhập lại được.

## Buổi 6 — Zalo thật (khi DNS đã chuyển sang Cloudflare)
- [ ] 6.1 Tunnel `dev-api.haulp.io.vn` chạy, Zalo App đã khai báo Callback + Webhook, đã lưu OA Secret Key.
- [ ] 6.2 Kết nối kênh → Kết nối Zalo OA → trạng thái "Đang hoạt động".
- [ ] 6.3 Zalo cá nhân nhắn OA → bot trả lời trên Zalo trong vài giây (làm lại các mục 1.1–1.10 trên Zalo).
- [ ] 6.4 Nhân viên trả lời từ Hộp thư → khách nhận trên Zalo; nhân viên trả lời trong app OA → bot dừng.
- [ ] 6.5 Ngắt kết nối → bot ngừng trả lời.

---

## Chưa có / đang chờ (không cần test)
- **Báo giá khi bot chủ động nhắn** — chưa làm.
- **ZNS / ZBS Template** (nhắn trước theo SĐT/UID) — chờ docs API.
- **Hạn mức hội thoại** — chỉ thấy khi có khách Zalo thật (đã có test tự động).
