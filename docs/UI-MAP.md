# Sơ đồ giao diện trang quản trị (cập nhật 2026-11-13)

Mỗi tính năng nằm đúng một chỗ. Menu chia theo việc trong ngày; trang con (ví dụ `/settings/team`) sáng mục cha.
Cấu hình menu: `zalo-ai-portal/src/components/shell/nav-config.ts`. Mỗi trang có nút **"Cách dùng trang này"** (hướng dẫn ngắn).
Máy tính: thanh bên trắng (điểm nhấn xanh Zalo) thu gọn được (nút ghim hoặc Ctrl+B); trang Hộp thư trên màn < 1600px tự thu thanh bên.

## Menu chính (chủ doanh nghiệp)

| Nhóm | Mục | Đường dẫn | Có gì |
| --- | --- | --- | --- |
| — | Tổng quan | `/` | Cần xử lý ngay · Hôm nay (doanh thu, lịch, khách mới, hóa đơn, lịch sắp tới) · Trợ lý AI 30 ngày · Thiết lập hệ thống · Hoạt động của bot. Nhân viên vào thẳng Hộp thư |
| — | Báo cáo | `/reports` | Quyền "Xem báo cáo". Ô **Hỏi số liệu** (tiếng Việt → mở đúng báo cáo) · tab `?tab=` Tổng quan · Doanh thu (ngày/tuần/tháng, theo dịch vụ/nhân viên/chi nhánh/loại/cách trả/nguồn, Excel) · Khách hàng · Lấp đầy lịch (theo ngày, bản đồ nhiệt giờ đông, gợi ý) · Trợ lý AI · Công nợ. Khoảng ngày + chi nhánh chung, lưu trên URL (`?from=&to=&branch=`). Tab có tiền cần "Xem doanh thu toàn công ty" |
| Vận hành | Lịch hẹn | `/appointments` | Lịch ngày/tuần/danh sách, đặt lịch (`?new=1` mở sẵn), hàng chờ, check-in, thanh toán từ lịch |
| | Hộp thư | `/inbox` | Kiểu Messenger, chiếm trọn màn: danh sách (tìm, lọc) · khung chat · thông tin khách + "Vì sao bot trả lời vậy?" (màn ≥ 1280px, màn hẹp mở bằng menu ⋯). Tiếp quản, trả lại bot, giao nhân viên, gõ `/` chèn tin nhắn mẫu; hội thoại Zalo cá nhân ghi tên nick, có dải "Bot tạm im" khi chủ nick vừa nhắn tay. Huy hiệu = khách chờ nhân viên |
| | Bán hàng | `/sales` | Hóa đơn (`?new=1` mở sẵn), ca thu ngân, voucher |
| Khách hàng | Khách hàng | `/customers` | Danh sách theo tầng, hồ sơ (`/customers/[id]`: chat, lịch, gói, ví, hóa đơn, nhật ký chăm sóc), nhập danh sách (`/customers/import`) |
| | Cần chăm sóc | `/care` | Bot đã nhắn / cần nhân viên liên hệ, tin nháp, tới hẹn chưa nhắn OA được. Huy hiệu = gợi ý đang mở |
| | Chăm sóc Zalo cá nhân | `/zalo-care` | Chưa có nick thì nhắc kết nối ở Kênh Zalo. Dải gọn từng nick (trạng thái, người mới / lời mời / tin gửi hôm nay so với giới hạn, phanh, người chờ + dự kiến; chủ DN có link **Quản lý nick** sang Kênh Zalo) · tab **Thêm SĐT** (ô dán hàng loạt, nguồn, ghi chú cho AI, người giới thiệu, ô xác nhận bắt buộc) · tab **Đang chăm sóc** (`?tab=list`: lọc trạng thái/nhân viên/nick/đợt dán, đổi phụ trách, dừng; "gọi điện" = việc của nhân viên). Nhân viên dùng được |
| Trợ lý AI | Chat thử | `/chat-test` | Đóng vai khách, "Vì sao?", tab Giọng văn; giao diện điện thoại `/phone-test` |
| | Kho kiến thức | `/knowledge` | Dữ liệu, nhập file (so sánh rồi áp dụng), lịch sử, tài liệu, thử tìm kiếm |
| | Thiết lập trợ lý | `/assistant` | Giọng văn & xưng hô · `/assistant/handoff` Chuyển nhân viên (giờ làm việc, câu chuyển tiếp) · `/assistant/care` Chăm sóc chủ động |
| | Kênh Zalo | `/channels` | **Tường tài khoản** (2026-11-13): mọi tài khoản Zalo (OA + nick cá nhân) là một **thẻ mực** cùng khung — tai trạng thái ở góc, số khách đã chăm hôm nay + cột 7 ngày, nút chanh = việc tiếp theo (OA: Kết nối lại / Mở Hộp thư; nick: Quét lại mã QR / Kết nối lại / Chăm sóc khách), menu ⋯ (nick: Nhịp gửi, Người quen nhắn, Cài đặt, Ngắt kết nối; OA: Ngắt kết nối). Thẻ nick có 3 cột số hôm nay + chế độ an toàn tóm tắt. Đầu trang 3 ô: kênh đang chạy · cần bạn xử lý · khách hôm nay (điện thoại: một câu). Ô viền đứt **Thêm kênh Zalo** mở hộp chọn OA (khuyên dùng) / nick cá nhân (cảnh báo rủi ro → mã QR); chưa có kênh nào thì hai lựa chọn hiện thẳng trên trang. Cuối trang: lưu ý theo loại kênh (OA | cá nhân). Khung "Giả lập Zalo cá nhân" chỉ ở môi trường dev |
| Quản lý | Danh mục | `/catalog` | Dịch vụ, gói liệu trình, sản phẩm, giá |
| | Kho hàng | `/inventory` | Tồn, nhập hàng, chuyển kho, kiểm kê, định mức vật tư, lịch sử |
| | Nhân viên | `/staff` | 4 tab (đường dẫn riêng): Hồ sơ & ca làm (`/staff`) · Hiệu suất (`/staff/performance`: dẫn đầu, bảng xếp hạng sắp xếp được) · Hoa hồng (`/staff/commissions?period=yyyy-MM`: bảng kê từng người, đánh dấu đã trả, quy tắc tính) · Chấm công (`/staff/attendance`: nút Vào ca/Ra ca + đồng hồ, ai đang trong ca, nhật ký, sửa giờ). Nhân viên thường chỉ thấy số của mình |
| (chân thanh bên) | Cài đặt | `/settings` | Doanh nghiệp · `/settings/branches` Chi nhánh & giờ mở cửa (giường/phòng) · `/settings/team` Tài khoản & phân quyền · `/settings/booking` Lịch hẹn & bán hàng · `/settings/payments` Thanh toán (VietQR) · `/settings/notifications` Thông báo (Telegram, nhắc chờ lâu) · `/settings/quick-replies` Tin nhắn mẫu (gõ `/` trong ô trả lời ở Hộp thư để chèn) |
| (thẻ người dùng) | Tài khoản | `/account` | Đổi mật khẩu |

## Quản trị hệ thống (super admin)

| Mục | Đường dẫn | Có gì |
| --- | --- | --- |
| Doanh nghiệp thuê | `/admin`, `/admin/[id]` | Tạo doanh nghiệp, gói, hạn thuê, hạn mức, khóa/mở, xuất/xóa dữ liệu, chi phí AI |
| Proxy Zalo cá nhân | `/admin/proxies` | Kho proxy (trống / đang gán / lỗi / đang nghỉ 7 ngày), thêm proxy (mật khẩu chỉ gửi lên một lần), gán/đổi proxy cho từng nick |
| Hàng đợi job | `/hangfire` (trang của BE) | Theo dõi job chạy nền |

## Điện thoại

Thanh dưới: Tổng quan · Lịch hẹn · Hộp thư · Khách hàng · **Thêm** (mọi mục kèm một dòng giải thích, đổi doanh nghiệp, đăng xuất).

## Chưa có giao diện

- C7, C8 (chưa làm BE).
