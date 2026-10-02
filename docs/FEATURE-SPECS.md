# Đặc tả tính năng — Trợ lý Zalo AI

> Lập ngày 2026-10-01, từ các buổi trao đổi với chủ dự án. Mục nào ghi **(đề xuất)** là Claude chọn mặc định, chủ dự án có thể đổi.
> Quy tắc theo ngành: xem `INDUSTRIES.md`. Lộ trình: xem `ROADMAP.md`.

---

## 1. Câu chuyển tiếp giữa bot và nhân viên — Phase 5

**Vấn đề:** Zalo không tự báo khách đang nói chuyện với ai. Mọi tin từ OA (bot hay nhân viên) đều hiện dưới tên OA. Hệ thống phải tự gửi câu thông báo để khách biết đang nói với AI hay người, và chờ bao lâu.

| # | Tình huống | Người gửi | Câu mẫu mặc định | Bật/tắt |
| --- | --- | --- | --- | --- |
| 1 | Bot tự chuyển người (khách đòi gặp người, bot không chắc, khách bực bội, phàn nàn) | Bot | "Dạ em xin phép chuyển {xung_ho} sang chuyên viên chăm sóc khách hàng của {ten_doanh_nghiep}. Chuyên viên sẽ phản hồi trong khoảng {thoi_gian_phan_hoi} ạ." | **Luôn bật** |
| 1b | Như trên nhưng ngoài giờ làm việc | Bot | "...Hiện đã ngoài giờ làm việc, chuyên viên sẽ liên hệ lại {xung_ho} vào {gio_mo_cua_tiep_theo} ạ." | Luôn bật |
| 1c | Khẩn cấp (dấu hiệu nguy hiểm, xem INDUSTRIES.md mục 3) | Bot | Câu khẩn cấp theo mẫu ngành (khuyên đến cơ sở y tế / gọi 115...) + "Em đã báo ngay cho người phụ trách." | Luôn bật |
| 2 | Nhân viên bấm "Tiếp quản" giữa lúc bot đang trả lời | Hệ thống, gửi trước tin đầu của nhân viên | "Dạ chào {xung_ho}, em là {ten_nhan_vien}, chuyên viên chăm sóc khách hàng của {ten_doanh_nghiep}. Em sẽ hỗ trợ {xung_ho} tiếp ạ." | Mặc định bật |
| 3 | Nhân viên chủ động nhắn từ trang "Cần chăm sóc" | Lời giới thiệu ghép vào đầu tin nháp | "Dạ {ten_khach} ơi, em là {ten_nhan_vien}, chuyên viên của {ten_doanh_nghiep}. ..." | Mặc định bật |
| 4 | Trả lại cho bot | Bot | "Dạ nếu {xung_ho} cần thêm thông tin, trợ lý AI của {ten_doanh_nghiep} luôn sẵn sàng hỗ trợ 24/7 ạ." | Mặc định tắt |

Cấu hình theo tenant (trang Cài đặt → Chuyển tiếp):
- Sửa nội dung từng câu, biến: `{xung_ho}`, `{ten_khach}`, `{ten_nhan_vien}`, `{ten_doanh_nghiep}`, `{thoi_gian_phan_hoi}`, `{gio_mo_cua_tiep_theo}`.
- Hiện tên thật nhân viên hay chỉ "chuyên viên".
- **Chữ ký** cuối tin nhân viên (mặc định bật): "— {ten_nhan_vien}, CSKH". Mỗi nhân viên tự đặt tên hiển thị.
- Giờ làm việc + thời gian phản hồi dự kiến.

Quy tắc:
- Nội dung câu chuyển tiếp cũng chạy qua bộ lọc câu cấm.
- Lưu câu chuyển tiếp như tin `sender = system` để báo cáo.

**Rủi ro cần kiểm tra ở Phase 4:** nhân viên trả lời thẳng trong app Zalo OA (không qua portal). Cần xác minh trong docs Zalo webhook có sự kiện "OA gửi tin" không:
- Có → hệ thống tự chuyển hội thoại sang `mode = human`, dừng bot.
- Không → hướng dẫn DN: nhân viên chỉ trả lời qua portal; ghi rõ trong tài liệu hướng dẫn.

---

## 2. Trang "Cần chăm sóc" — Phase 6

**Mục tiêu:** AI phân tích hội thoại, đưa ra danh sách khách mà **nhân viên** nên chủ động nhắn, kèm lý do, gợi ý và tin nháp.

### Khi nào AI phân tích
- Khi hội thoại "nguội": khách không nhắn trong một khoảng thời gian (cấu hình, mặc định 6 giờ), chỉ phân tích 1 lần cho mỗi đoạn hội thoại mới → tiết kiệm chi phí.
- Job định kỳ hằng ngày quét khách cũ (lâu không quay lại, còn buổi chưa dùng...).

### Kết quả phân tích (lưu bảng `care_suggestions`, có `tenant_id`)
```
{ contact_id, conversation_id, temperature: hot|warm|cold, reason, suggested_action,
  draft_message, trigger: price_no_close|thinking|asked_schedule|complaint_followup|win_back|unused_package,
  messaging_deadline, assigned_user_id, status: open|done|skipped|expired, outcome, created_at }
```

### Trường hợp đưa vào danh sách
| Trigger | Ví dụ |
| --- | --- |
| Hỏi giá chưa chốt | Hỏi giá gói triệt lông, không đặt |
| "Để suy nghĩ" | Khách nói sẽ cân nhắc |
| Hỏi lịch chưa đặt | Hỏi lịch trống thứ 7 rồi im |
| Theo dõi sau phàn nàn | Phàn nàn đã xử lý, cần hỏi lại sau 2–3 ngày |
| Kéo khách cũ | 60 ngày không quay lại (theo mẫu ngành) |
| Còn gói chưa dùng | Còn 3 buổi trong liệu trình |

### Giao diện
```
🔥 Chị Lan                                   Nóng · 2 ngày chưa phản hồi
   Lý do: hỏi giá triệt lông nách, nói "để suy nghĩ", chưa đặt lịch
   Gợi ý: gửi ưu đãi khách mới + lịch trống cuối tuần
   Tin nháp: "Dạ chị Lan ơi, em là Hương, chuyên viên của Spa Hoa Mai..."
   ⏱ Còn nhắn được qua OA: 3 ngày
   Giao cho: Hương        [Mở hội thoại] [Nhắn ngay] [Đã xử lý] [Bỏ qua]
```
- Lọc: của tôi / tất cả / theo mức độ / sắp hết hạn nhắn.
- Sắp xếp mặc định: sắp hết hạn nhắn OA → nóng → mới.
- "Nhắn ngay" mở khung soạn với tin nháp; nhân viên **sửa rồi mới gửi**, không tự gửi.
- Gửi xong: thẻ chuyển "Đã xử lý" (hoặc tự chuyển khi khách trả lời); ghi kết quả (đặt lịch / mua / từ chối / không phản hồi) để báo cáo tỷ lệ chốt.

### Quyết định (đề xuất)
- **Tin nháp do AI viết sẵn: có**, tenant tắt được để tiết kiệm chi phí.
- **Gán khách cho nhân viên: có**, tự gán cho nhân viên đã chat gần nhất với khách; owner gán lại được. DN 1 người thì bỏ qua.

### Ràng buộc
- **Thời gian OA được nhắn tư vấn** sau tin cuối của khách có giới hạn. Phải kiểm tra docs Zalo (Phase 4) và ghi vào `zalo-api-notes.md`, **không đoán**. Quá hạn → không cho "Nhắn ngay", gợi ý ZNS (GĐ2) hoặc gọi điện.
- Gợi ý mang tính quảng cáo (ưu đãi, kéo khách cũ) chỉ hiện với khách **đã đồng ý nhận tin**, không hiện với khách đã nhắn "hủy". Ranh giới chăm sóc/quảng cáo cần luật sư xác nhận.
- Không phân tích lại hội thoại đang `mode = human`.

---

## 3. Gợi ý gói và giải thích tình trạng (bệnh) — Phase 3

- **Khách tự hỏi** → bot gợi ý gói từ danh mục DN nạp, dựa trên nhu cầu khách nói ra và lịch sử dịch vụ đã dùng. Có từ Phase 3.
- **Giải thích bệnh/tình trạng cơ thể** → chỉ với ngành có bộ an toàn y tế, chỉ dùng tài liệu đã được chuyên môn duyệt (INDUSTRIES.md mục 3). Luôn kết bằng lời mời thăm khám trực tiếp.
- **Bot chủ động hỏi thăm + gợi ý gói** → flow chăm sóc, Giai đoạn 2.

Ví dụ đúng:
> **Khách:** Mặt chị có mấy đốm nâu, có phải nám không em?
> **Bot:** Dạ đốm nâu có thể do nám, tàn nhang hoặc đốm nâu do nắng, mỗi loại xử lý khác nhau nên cần bác sĩ soi da mới xác định được ạ. Bên em có dịch vụ soi da miễn phí thứ 3 và thứ 7, sau đó bác sĩ sẽ tư vấn liệu trình phù hợp. Chị có muốn em đặt lịch không ạ?

Ví dụ khẩn cấp:
> **Khách:** Hôm qua tiêm filler giờ môi tím tái, đau lắm.
> **Bot:** Dạ đây có thể là dấu hiệu cần xử lý y tế khẩn cấp. Chị vui lòng đến ngay cơ sở y tế gần nhất hoặc gọi 115. Em đã báo ngay cho bác sĩ phụ trách của bên em.

---

## 4. Tình huống chăm sóc khách — tổng hợp

| # | Tình huống | Ai nhắn | Có khi nào |
| --- | --- | --- | --- |
| 1 | Hỏi giá chưa chốt → nhân viên nhắn theo gợi ý | Nhân viên | Phase 6 |
| 2 | Nhắc lịch hẹn trước 1 ngày | Bot tự động | GĐ2 (cần tính năng lịch hẹn). Trước đó: nhân viên nhắn tay |
| 3 | Hỏi thăm sau dịch vụ + xin đánh giá; phàn nàn → chuyển người | Bot, chuyển người khi cần | Flow: GĐ2. Chuyển người khi bực bội: Phase 5 |
| 4 | Kéo khách cũ / còn gói chưa dùng | Nhân viên (còn hạn nhắn) hoặc ZNS (quá hạn) | Gợi ý: Phase 6. ZNS: GĐ2 |
| 5 | Khách nhắn "hủy" → dừng tin chăm sóc/quảng cáo | Bot | GĐ2 (cùng flow). Khách vẫn được trả lời khi tự nhắn |

**Quyết định (đề xuất):** nhắc lịch tự động để Giai đoạn 2. Có thể đưa lên v1 nếu ngành đầu tiên là spa/nha khoa, khi đó Phase 6 kéo dài thêm 1–2 tuần cho tính năng lịch hẹn.

Điều kiện trước mọi tin bot tự gửi (GĐ2): khách đã đồng ý nhận tin, trong khung giờ cho phép, chưa vượt tần suất tối đa, chưa có điều kiện dừng (đã mua, đã phản hồi, đã hủy), còn hạn nhắn OA hoặc có mẫu ZNS phù hợp. Mọi lần gửi ghi `flow_runs`.
