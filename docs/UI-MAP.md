# Sơ đồ giao diện trang quản trị (cập nhật 2026-11-01)

Mỗi tính năng nằm đúng một chỗ. Menu chia theo việc trong ngày; trang con (ví dụ `/settings/team`) sáng mục cha.
Cấu hình menu: `zalo-ai-portal/src/components/shell/nav-config.ts`. Mỗi trang có nút **"Cách dùng trang này"** (hướng dẫn ngắn).
Máy tính: thanh bên trắng (điểm nhấn xanh Zalo) thu gọn được (nút ghim hoặc Ctrl+B); trang Hộp thư trên màn < 1600px tự thu thanh bên.

## Menu chính (chủ doanh nghiệp)

| Nhóm | Mục | Đường dẫn | Có gì |
| --- | --- | --- | --- |
| — | Tổng quan | `/` | Cần xử lý ngay · Hôm nay (doanh thu, lịch, khách mới, hóa đơn, lịch sắp tới) · Trợ lý AI 30 ngày · Thiết lập hệ thống · Hoạt động của bot. Nhân viên vào thẳng Hộp thư |
| Vận hành | Lịch hẹn | `/appointments` | Lịch ngày/tuần/danh sách, đặt lịch (`?new=1` mở sẵn), hàng chờ, check-in, thanh toán từ lịch |
| | Hộp thư | `/inbox` | Kiểu Messenger, chiếm trọn màn: danh sách (tìm, lọc) · khung chat · thông tin khách + "Vì sao bot trả lời vậy?" (màn ≥ 1280px, màn hẹp mở bằng menu ⋯). Tiếp quản, trả lại bot, giao nhân viên. Huy hiệu = khách chờ nhân viên |
| | Bán hàng | `/sales` | Hóa đơn (`?new=1` mở sẵn), ca thu ngân, voucher |
| Khách hàng | Khách hàng | `/customers` | Danh sách theo tầng, hồ sơ (`/customers/[id]`: chat, lịch, gói, ví, hóa đơn, nhật ký chăm sóc), nhập danh sách (`/customers/import`) |
| | Cần chăm sóc | `/care` | Bot đã nhắn / cần nhân viên liên hệ, tin nháp, tới hẹn chưa nhắn OA được. Huy hiệu = gợi ý đang mở |
| Trợ lý AI | Chat thử | `/chat-test` | Đóng vai khách, "Vì sao?", tab Giọng văn; giao diện điện thoại `/phone-test` |
| | Kho kiến thức | `/knowledge` | Dữ liệu, nhập file (so sánh rồi áp dụng), lịch sử, tài liệu, thử tìm kiếm |
| | Thiết lập trợ lý | `/assistant` | Giọng văn & xưng hô · `/assistant/handoff` Chuyển nhân viên (giờ làm việc, câu chuyển tiếp) · `/assistant/care` Chăm sóc chủ động |
| | Kênh Zalo | `/channels` | Kết nối / ngắt Zalo OA |
| Quản lý | Danh mục | `/catalog` | Dịch vụ, gói liệu trình, sản phẩm, giá |
| | Kho hàng | `/inventory` | Tồn, nhập hàng, chuyển kho, kiểm kê, định mức vật tư, lịch sử |
| | Nhân viên | `/staff` | Hồ sơ nhân viên, dịch vụ làm được, ca làm, nghỉ phép |
| (chân thanh bên) | Cài đặt | `/settings` | Doanh nghiệp · `/settings/branches` Chi nhánh & giờ mở cửa (giường/phòng) · `/settings/team` Tài khoản & phân quyền · `/settings/booking` Lịch hẹn & bán hàng · `/settings/payments` Thanh toán (VietQR) · `/settings/notifications` Thông báo (Telegram, nhắc chờ lâu) |
| (thẻ người dùng) | Tài khoản | `/account` | Đổi mật khẩu |

## Quản trị hệ thống (super admin)

| Mục | Đường dẫn | Có gì |
| --- | --- | --- |
| Doanh nghiệp thuê | `/admin`, `/admin/[id]` | Tạo doanh nghiệp, gói, hạn thuê, hạn mức, khóa/mở, xuất/xóa dữ liệu, chi phí AI |
| Hàng đợi job | `/hangfire` (trang của BE) | Theo dõi job chạy nền |

## Điện thoại

Thanh dưới: Tổng quan · Lịch hẹn · Hộp thư · Khách hàng · **Thêm** (mọi mục kèm một dòng giải thích, đổi doanh nghiệp, đăng xuất).

## Chưa có giao diện

- Báo cáo (C6): API `/reports/*` đã có; Tổng quan đang dùng `/reports/today` và `/reports/ai`. Trang Báo cáo đầy đủ làm cùng FE C5/C6.
- Hoa hồng, chấm công, hiệu suất (C5): API đã có, chưa có trang.
