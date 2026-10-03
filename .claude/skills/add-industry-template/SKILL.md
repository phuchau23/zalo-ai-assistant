---
name: add-industry-template
description: Thêm mẫu ngành mới (spa, nha khoa, bất động sản, sửa chữa nhà...). Dùng khi chủ dự án muốn bot hỗ trợ ngành mới hoặc chỉnh kịch bản của ngành có sẵn.
---

# Thêm mẫu ngành

Đọc `docs/INDUSTRIES.md` trước (mức rủi ro, nội dung từng ngành, quy trình mở ngành). Không train model; ngành = mẫu ngành + kho kiến thức của DN.

Tạo `src/ZaloAi.IndustryTemplates/Templates/<slug>/` (file nhúng `EmbeddedResource`) gồm:
- `template.json`: slug, tên, mức rủi ro (low|medium|high), `medicalSafety` (bool), phiên bản.
- `persona.md`: vai trò, giọng văn, xưng hô mặc định.
- `rules.md`: điều bot phải làm/không làm trong ngành.
- `forbidden.json`: cụm từ cấm (ví dụ ngành y, thẩm mỹ: "cam kết", "trị dứt điểm", "100%", "không tác dụng phụ"), kèm câu thay thế.
- `danger_signals.json`: dấu hiệu cần chuyển người gấp/khẩn cấp + câu trả lời khẩn cấp.
- `lead_fields.json`: thông tin cần thu thập (ví dụ sửa nhà: địa chỉ công trình, hạng mục, diện tích, thời gian muốn làm).
- `required_docs.md`: tài liệu DN phải nạp trước khi bật bot.
- `faq_sample.md`: dữ liệu mẫu để demo.
- `care_flows.json` (GĐ2): flow chăm sóc mẫu.
- `evals.json`: câu hỏi thường + câu bẫy; ngành `medicalSafety` thêm ≥ 20 câu an toàn (phải đạt 100%).

Đăng ký slug trong `IndustryTemplateRegistry.cs` (tạo ở M3) **và** trong `IndustryCatalog.cs` (danh sách ngành cho ô chọn ở trang cài đặt, validator dùng để kiểm `industrySlug`).

Viết test: prompt builder nạp đúng template; câu trả lời chứa cụm cấm bị chặn; dấu hiệu nguy hiểm kích hoạt chuyển người khẩn cấp.

Ngành mức "chưa mở" trong INDUSTRIES.md → không làm, báo chủ dự án hỏi luật sư. Lưu ý tenant mẫu "Khoa Học Nguyệt Đạo" (bấm huyệt, massage) xếp `spa`; nếu DN quảng cáo chữa bệnh thì thành "chưa mở".
