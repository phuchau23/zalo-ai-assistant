# Sơ đồ giao diện trang quản trị (cập nhật 2026-11-15)

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
| Kênh Zalo | Zalo OA | `/channels` | Chỉ Zalo OA (tách khỏi nick cá nhân 2026-11-15). **Tường tài khoản**: mỗi OA một **thẻ mực** — tai trạng thái ở góc, số khách hôm nay + cột 7 ngày, nút chanh = việc tiếp theo (Kết nối lại / Mở Hộp thư), menu ⋯ Ngắt kết nối. Đầu trang 3 ô: OA đang chạy · cần bạn xử lý · khách hôm nay (điện thoại: một câu). Ô viền đứt **Kết nối thêm Zalo OA** (sang trang cấp quyền của Zalo); chưa có OA thì thẻ giới thiệu + nút kết nối. Thẻ chỉ đường "Nick Zalo cá nhân đã chuyển sang trang Zalo cá nhân →". Cuối trang: lưu ý khi dùng OA. Nhân viên chỉ xem |
| | Zalo cá nhân | `/zalo-personal` | Nhân viên dùng được (thêm nick chỉ chủ DN). Đầu trang: **+ Thêm nick** (chủ DN: cảnh báo rủi ro → mã QR) · **Dán danh sách SĐT**. Tab trên URL `?tab=`: **Tổng quan** (5 ô: nick đang chạy x/y · đang chăm sóc · đã trả lời · có lịch hẹn · tin gửi hôm nay — điện thoại gộp một hàng; lưới thẻ nick: viên trạng thái có chấm nhịp đập khi đang gửi / Ngoài giờ gửi "Bắt đầu lúc 08:00 mai" / Phanh chậm / Tạm dừng / Mất kết nối – cần quét lại QR (thẻ viền đỏ + nút sửa lỗi), số lớn "Đang chăm sóc N người", thanh phễu Chờ lượt → Đã mời/kết bạn → Đã nhắn → Đã trả lời + chú thích có số, viên "có lịch hẹn", dòng kết thúc (không có Zalo, im lặng, từ chối, hết hạn), thanh hôm nay (người mới, tin), dòng "Vừa xong", chủ DN thấy thêm dòng **AI tháng này: 288 đ**, nút **Thêm số cho nick này** / **Chi tiết →**; chủ DN có ô thứ 6 **Chi phí AI tháng này** (tổng các nick cá nhân); ô viền đứt Thêm nick; tự tải lại mỗi 20 giây) · **Thêm số** (`?tab=add`, `&nick=<id>` = giao cho nick đó: ô dán, chọn **Chia tự động cho các nick** — mặc định khi ≥ 2 nick đang chạy, tick chọn nick, nick không nhận số ghi lý do — hoặc **Giao cho một nick**; khung **Xem trước** gọi BE `dryRun` sau ~0,7 giây: số hợp lệ, bỏ qua vì sao, mỗi nick nhận bao nhiêu + xong khoảng mấy ngày; nguồn, ghi chú cho AI, người giới thiệu, ô xác nhận bắt buộc; kết quả kèm cách chia + "Xem danh sách đợt này") · **Đang chăm sóc** (`?tab=list`: lọc trạng thái/nhân viên/nick/đợt dán; chọn nhiều → đổi phụ trách, **Chuyển sang nick…** (chỉ người đang chờ lượt, báo số đã chuyển), dừng). `/zalo-care` cũ chuyển về đây (giữ `?tab=`) |
| | (chi tiết nick) | `/zalo-personal/[id]` | Đầu trang: tên + trạng thái + việc cần làm (chủ DN: Quét lại mã QR / Kết nối lại, Ngắt kết nối) + **Thêm số cho nick này**. Phễu lớn (đang chăm sóc, 4 bước có số, có lịch hẹn, kết thúc, người chờ lượt + dự kiến/giờ chạy lại) + nút **Chia lại hàng chờ cho nick khác** (xác nhận; chỉ người đang chờ lượt; kết quả theo nick; báo khi không còn nick nào khác đang chạy) · Hôm nay (lời mời, người mới, tin so với giới hạn; phanh tự động; tỷ lệ 7 ngày; bậc nick mới) · **Chi phí AI** (chỉ chủ DN, nhân viên không gọi API: tiền VND trong kỳ Tháng này / Tháng trước / 30 ngày qua, ≈ USD, số lần gọi AI; đ / người đã nhắn, đ / khách trả lời, đ / lịch hẹn; cột chi phí theo ngày — rê/chạm/phím mũi tên xem ngày, USD, tỷ giá ngày đó; chú thích giá bán USD Vietcombank từng ngày + phí thẻ + ngày dùng tỷ giá gần nhất) · Hoạt động gần đây (5 việc, thời gian tương đối) · **Cài đặt nick** (chuyển từ trang Kênh: Nhịp gửi, Bot tạm im khi nhắn tay + bạn bè là khách?, Bạn bè chờ phân loại, IP riêng — chủ DN sửa qua hộp thoại, nhân viên chỉ xem) · **Người của nick này** (bảng Đang chăm sóc lọc sẵn theo nick). Khung "Giả lập Zalo cá nhân" chỉ ở môi trường dev |
| Trợ lý AI | Chat thử | `/chat-test` | Đóng vai khách, "Vì sao?", tab Giọng văn; giao diện điện thoại `/phone-test` |
| | Kho kiến thức | `/knowledge` | Dữ liệu, nhập file (so sánh rồi áp dụng), lịch sử, tài liệu, thử tìm kiếm |
| | Thiết lập trợ lý | `/assistant` | Giọng văn & xưng hô · `/assistant/handoff` Chuyển nhân viên (giờ làm việc, câu chuyển tiếp) · `/assistant/care` Chăm sóc chủ động |
| Quản lý | Danh mục | `/catalog` | Dịch vụ, gói liệu trình, sản phẩm, giá |
| | Kho hàng | `/inventory` | Tồn, nhập hàng, chuyển kho, kiểm kê, định mức vật tư, lịch sử |
| | Nhân viên | `/staff` | 4 tab (đường dẫn riêng): Hồ sơ & ca làm (`/staff`) · Hiệu suất (`/staff/performance`: dẫn đầu, bảng xếp hạng sắp xếp được) · Hoa hồng (`/staff/commissions?period=yyyy-MM`: bảng kê từng người, đánh dấu đã trả, quy tắc tính) · Chấm công (`/staff/attendance`: nút Vào ca/Ra ca + đồng hồ, ai đang trong ca, nhật ký, sửa giờ). Nhân viên thường chỉ thấy số của mình |
| (chân thanh bên) | Cài đặt | `/settings` | Doanh nghiệp · `/settings/branches` Chi nhánh & giờ mở cửa (giường/phòng) · `/settings/team` Tài khoản & phân quyền · `/settings/booking` Lịch hẹn & bán hàng · `/settings/payments` Thanh toán (VietQR) · `/settings/notifications` Thông báo (Telegram, nhắc chờ lâu) · `/settings/quick-replies` Tin nhắn mẫu (gõ `/` trong ô trả lời ở Hộp thư để chèn) |
| (thẻ người dùng) | Tài khoản | `/account` | Đổi mật khẩu |

## Quản trị hệ thống (super admin)

| Mục | Đường dẫn | Có gì |
| --- | --- | --- |
| Doanh nghiệp thuê | `/admin`, `/admin/[id]` | Tạo doanh nghiệp, gói, hạn thuê, hạn mức, khóa/mở, xuất/xóa dữ liệu, chi phí AI 12 tháng · **Chi phí AI theo nick** (kỳ Tháng này / Tháng trước / 30 ngày qua: mỗi nick tên, kênh, VND, USD, lần gọi AI, người đã nhắn, đ/người, thanh tỷ lệ; dòng "Không thuộc nick nào" = chat thử, nạp kho kiến thức, hỏi số liệu; dòng tổng; chú thích tỷ giá) |
| Proxy Zalo cá nhân | `/admin/proxies` | Kho proxy (trống / đang gán / lỗi / đang nghỉ 7 ngày), thêm proxy (mật khẩu chỉ gửi lên một lần), gán/đổi proxy cho từng nick |
| Hàng đợi job | `/hangfire` (trang của BE) | Theo dõi job chạy nền |

## Điện thoại

Thanh dưới: Tổng quan · Lịch hẹn · Hộp thư · Khách hàng · **Thêm** (mọi mục kèm một dòng giải thích, đổi doanh nghiệp, đăng xuất).

## Chưa có giao diện

- C7, C8 (chưa làm BE).
