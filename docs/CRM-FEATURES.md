# Tính năng CRM + AI chăm sóc khách hàng

> **Chủ dự án duyệt 2026-10-15: làm ĐẦY ĐỦ tất cả (kể cả mục 🔮), không dừng ở MVP.** Chốt thêm: nhiều chi nhánh ngay từ đầu;
> quản lý tồn kho khi bán sản phẩm; nhân viên mặc định chỉ xem doanh số của mình (sếp bật thêm quyền); lịch theo nhân viên **và**
> giường/phòng. Thứ tự làm, thời gian và schema: `CRM-PLAN.md`. Các mục chờ bên thứ ba (hóa đơn điện tử, đọc số dư ngân hàng,
> ZNS, Mini App) làm khi có đối tác/docs. Ký hiệu ⭐ ➕ 🔮 dưới đây giờ chỉ còn ý nghĩa **thứ tự ưu tiên trong từng đợt**.

Đối tượng: doanh nghiệp dịch vụ có lịch hẹn (spa, dưỡng sinh, nha khoa, phòng khám thẩm mỹ, salon, gym/yoga, trung tâm trị liệu).
Hai vai trò: **Chủ doanh nghiệp (sếp)** và **Nhân viên** — kèm công tắc quyền chi tiết để sếp tự bật/tắt.

Ký hiệu ưu tiên:
- ⭐ **Bản đầu (MVP)** — cần có để bán được cho khách đầu tiên.
- ➕ **Nâng cao** — làm ngay sau MVP, tăng giá trị gói Pro.
- 🔮 **Để sau** — khi có nhiều khách trả tiền / có nhu cầu thật.
- 🤖 = có AI tham gia (điểm khác biệt so với phần mềm quản lý spa thông thường).

---

## 1. Lịch hẹn

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Lịch ngày / tuần | Mỗi nhân viên một cột, mỗi lịch một ô màu theo trạng thái; xem trên điện thoại được | ⭐ |
| Giờ mở cửa, ngày nghỉ | Theo từng thứ; ngày lễ; nhân viên nghỉ phép | ⭐ |
| Đặt lịch tay | Lễ tân/nhân viên tạo lịch khi khách gọi điện, đến trực tiếp | ⭐ |
| 🤖 Bot tự chốt lịch trên Zalo | Bot hỏi dịch vụ, giờ, tên → **hệ thống** kiểm tra chỗ trống (không để AI đoán) → chốt; kín thì gợi ý 2–3 giờ gần nhất | ⭐ |
| 🤖 Đổi / hủy lịch qua chat | Theo chính sách hủy/đổi của DN (vd trước 2 giờ) | ⭐ |
| Chống trùng lịch | Kiểm tra nhân viên rảnh + giờ mở cửa + thời lượng dịch vụ + thời gian dọn giữa 2 khách | ⭐ |
| Trạng thái lịch | Đã đặt → Đã đến (check-in) → Đang làm → Hoàn thành; Không đến; Đã hủy | ⭐ |
| 🤖 Nhắc lịch tự động | Trước 1 ngày và 2 giờ; khách trả lời "ok" / "dời" bot tự xử lý | ⭐ |
| 🤖 Tóm tắt khách trước buổi làm | Bấm vào lịch: khách gặp vấn đề gì, muốn gì, lưu ý, buổi trước làm gì, gói còn mấy buổi | ⭐ |
| Kéo thả đổi giờ / đổi nhân viên | Trên lịch tuần | ➕ |
| Theo giường / phòng | Thêm cột tài nguyên ngoài nhân viên (spa có giới hạn giường, phòng xông) | ➕ |
| Lịch định kỳ | "Thứ 3 hằng tuần 18:00" cho khách đang làm gói 10 buổi | ➕ |
| Cọc giữ chỗ | Khung giờ đẹp hoặc khách hay bỏ hẹn → yêu cầu cọc qua mã QR chuyển khoản | ➕ |
| Đánh dấu khách hay bỏ hẹn | Bỏ hẹn ≥ 2 lần → cảnh báo, bắt cọc | ➕ |
| 🤖 Danh sách chờ | Khung kín → khách vào hàng chờ; có người hủy → bot nhắn khách chờ | 🔮 |
| Trang đặt lịch online | Link riêng của DN (gắn Facebook, website); sau này Zalo Mini App | 🔮 |

## 2. Bán hàng / Thu ngân

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Hóa đơn | Gồm dịch vụ lẻ, gói liệu trình, sản phẩm; tạo từ lịch hẹn bằng 1 nút "Thanh toán" | ⭐ |
| Giảm giá | Theo % hoặc số tiền, có lý do; nhân viên giảm quá X% cần sếp duyệt | ⭐ |
| Thanh toán nhiều cách | Tiền mặt, chuyển khoản, thẻ; trả một phần, ghi nợ | ⭐ |
| Mã QR chuyển khoản | Mã VietQR có sẵn số tiền + nội dung hóa đơn, khách quét là đúng | ⭐ |
| Phiếu thu gửi qua Zalo | Khách nhận phiếu thu ngay trong khung chat OA | ⭐ |
| Hủy / hoàn tiền hóa đơn | Bắt buộc ghi lý do, lưu nhật ký ai hủy | ⭐ |
| Chia doanh số | Một hóa đơn nhiều nhân viên làm → chia doanh số theo phần | ➕ |
| Đối soát cuối ngày | Chốt ca: tiền mặt trong két so với hệ thống | ➕ |
| Mã khuyến mãi / voucher | Mã giảm giá có hạn dùng, số lượt | ➕ |
| Tự xác nhận đã nhận chuyển khoản | Kết nối dịch vụ đọc biến động số dư ngân hàng | 🔮 |
| Hóa đơn điện tử | Kết nối nhà cung cấp hóa đơn điện tử được cấp phép (hỏi kế toán) | 🔮 |

## 3. Gói liệu trình & thẻ trả trước

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Bán gói theo buổi | Gói 10 buổi, hạn dùng; check-in tự trừ 1 buổi; hồ sơ hiện "còn 7/10" | ⭐ |
| 🤖 Gói sắp hết / sắp hết hạn | Bot nhắn khách gợi ý gia hạn, báo nhân viên | ⭐ |
| Bảo lưu, chuyển nhượng gói | Theo chính sách DN (vd chuyển cho người thân) | ➕ |
| Thẻ trả trước (ví) | Nạp 5 triệu được dùng 5,5 triệu; trừ dần khi thanh toán | ➕ |
| Ghi nhận doanh thu theo buổi | Gói bán trước: tiền thu ngay, doanh thu ghi dần mỗi buổi (báo cáo chuẩn kế toán) | ➕ |

## 4. Khách hàng 360°

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Hồ sơ, tầng, nhãn, nhật ký, lịch sử chat | Đã có | ✅ |
| Tổng chi tiêu, lần đến gần nhất, số lần đến | Tự tính từ hóa đơn và lịch hẹn | ⭐ |
| Gói đang dùng, công nợ, lịch sắp tới | Ngay đầu hồ sơ | ⭐ |
| Nguồn khách | Bot Zalo, khách vãng lai, giới thiệu, Facebook… → biết kênh nào ra tiền | ⭐ |
| Hồ sơ trị liệu | Ghi chú mỗi buổi (vùng đau, lực bấm, phản ứng); mã hóa vì là dữ liệu sức khỏe | ⭐ |
| Hạng thành viên | Bạc / Vàng / Kim cương tự lên hạng theo chi tiêu, ưu đãi theo hạng | ➕ |
| 🤖 Phân nhóm tự động | Khách VIP, khách mới, **sắp rời bỏ** (lâu không quay lại so với thói quen) | ➕ |
| Ảnh trước / sau | Chỉ khi khách đồng ý; mã hóa; chỉ người có quyền xem | 🔮 |
| Phiếu cam kết ký điện tử | Khách ký trên điện thoại trước khi làm dịch vụ đặc biệt | 🔮 |

## 5. Nhân viên

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Hồ sơ + dịch vụ làm được | Lịch chỉ xếp dịch vụ cho người làm được | ⭐ |
| Ca làm, ngày nghỉ | Lịch hẹn chỉ nhận vào giờ nhân viên có ca | ⭐ |
| Lịch của tôi | Nhân viên mở app thấy ngay khách hôm nay của mình + tóm tắt từng khách | ⭐ |
| Doanh số của tôi | Nhân viên thấy doanh số, số khách của riêng mình | ⭐ |
| Hoa hồng | Theo % dịch vụ / gói / sản phẩm hoặc theo tour; bảng hoa hồng tháng | ➕ |
| Hiệu suất | Doanh số, tỷ lệ khách quay lại, điểm hài lòng theo nhân viên | ➕ |
| Chấm công | Vào ca / ra ca trên điện thoại | 🔮 |

## 6. Kho hàng

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Danh mục sản phẩm bán lẻ | Giá bán, bán được trong hóa đơn | ⭐ |
| Tồn kho, nhập hàng, cảnh báo sắp hết | | 🔮 |
| Định mức vật tư theo dịch vụ | Mỗi buổi massage tốn bao nhiêu ml tinh dầu → tự trừ kho | 🔮 |

## 7. Chăm sóc & marketing tự động 🤖

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Bot trả lời 24/7, chuyển nhân viên, Cần chăm sóc | Đã có | ✅ |
| Chăm sóc sau buổi làm | Dựa trên **hóa đơn thật** (không cần ghi chú tay): 24h sau hỏi thăm theo đúng dịch vụ, dùng nội dung "Sau khi làm" | ⭐ |
| Khảo sát hài lòng | Sau buổi làm: chấm 1–5 sao; dưới 3 sao → báo sếp ngay | ⭐ |
| Kéo khách lâu không quay lại | Quá X ngày (theo thói quen của từng khách) → bot nhắn mời | ➕ |
| Chúc mừng sinh nhật + ưu đãi | | ➕ |
| Chiến dịch gửi hàng loạt | Theo nhóm khách, qua tin mẫu ZNS/ZBS (chờ docs Zalo), chỉ khách đã đồng ý nhận tin | 🔮 |
| Giới thiệu bạn bè | Mã giới thiệu, thưởng cho người giới thiệu | 🔮 |

## 8. Báo cáo cho sếp

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| Hôm nay | Lịch hẹn, khách đã đến / bỏ hẹn, doanh thu, tiền mặt, chuyển khoản | ⭐ |
| Doanh thu | Theo ngày / tuần / tháng, so với kỳ trước; theo dịch vụ, nhân viên, nguồn khách | ⭐ |
| 🤖 Hiệu quả của AI | Số lịch bot tự chốt, **doanh thu từ khách bot chốt**, số tin bot xử lý thay nhân viên | ⭐ |
| Xuất Excel | Mọi báo cáo | ⭐ |
| Khách | Mới / quay lại, tỷ lệ giữ chân, chi tiêu trung bình | ➕ |
| Tỷ lệ lấp đầy lịch | Giờ nào vắng → biết lúc nào nên chạy khuyến mãi | ➕ |
| 🤖 Báo cáo tuần tự động | Mỗi sáng thứ 2 gửi tóm tắt tuần qua Zalo / Telegram cho sếp | ➕ |
| 🤖 Hỏi số liệu bằng tiếng Việt | "Tháng này dịch vụ nào bán chạy nhất?", "Nhân viên nào nhiều khách quay lại nhất?" | 🔮 |
| Công nợ, gói chưa dùng hết | Tiền khách đã trả trước mà chưa sử dụng | ➕ |

## 9. Hệ thống & bảo mật

| Tính năng | Mô tả | Ưu tiên |
|---|---|---|
| 2 vai trò + công tắc quyền | Sếp bật/tắt cho nhân viên: xem doanh thu toàn công ty, giảm giá, hủy hóa đơn, xuất dữ liệu | ⭐ |
| Nhật ký thao tác | Ai hủy hóa đơn, sửa giá, xuất dữ liệu, xem hồ sơ khách | ⭐ |
| Dùng tốt trên điện thoại + cài như app | Nhân viên spa chủ yếu dùng điện thoại | ⭐ |
| Thông báo đẩy | Lịch mới bot chốt, khách đòi gặp người, đánh giá xấu | ➕ |
| Nhiều chi nhánh | Database chừa sẵn từ đầu; giao diện làm sau | 🔮 |

---

## Bản đầu đề xuất (MVP) — khoảng 6 tuần toàn thời gian

1. **Nền** (≈ 1 tuần): danh mục dịch vụ/gói/sản phẩm dùng chung với kho kiến thức, nhân viên + dịch vụ làm được + ca làm, giờ mở cửa, công tắc quyền.
2. **Lịch hẹn** (≈ 1,5 tuần): lịch ngày/tuần, đặt tay, bot tự chốt/đổi/hủy, chống trùng, check-in, nhắc lịch, tóm tắt khách.
3. **Bán hàng + gói** (≈ 1,5 tuần): hóa đơn, giảm giá, thanh toán + mã QR, phiếu thu qua Zalo, bán gói và trừ buổi, nguồn khách.
4. **Báo cáo** (≈ 1 tuần): Hôm nay, doanh thu, hiệu quả AI, "Lịch của tôi / Doanh số của tôi" cho nhân viên.
5. **AI dùng dữ liệu CRM** (≈ 1 tuần): chăm sóc sau buổi theo hóa đơn, khảo sát hài lòng, gói sắp hết.

## Gợi ý chia gói bán

| Gói | Gồm | Dành cho |
|---|---|---|
| **Cơ bản** | Lịch hẹn, bán hàng, gói liệu trình, khách hàng, báo cáo | DN chỉ cần phần mềm quản lý |
| **Pro (AI)** | Cơ bản + bot Zalo trả lời 24/7, tự chốt lịch, chăm sóc tự động, báo cáo hiệu quả AI | Điểm khác biệt chính, giá cao hơn |
| **Doanh nghiệp** | Pro + nhiều chi nhánh, hoa hồng nâng cao, kho hàng | Chuỗi |

Gói Cơ bản giúp bán dễ hơn (DN nào cũng cần quản lý lịch, tiền), rồi nâng lên Pro khi họ thấy AI chốt được lịch.
