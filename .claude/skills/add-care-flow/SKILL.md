---
name: add-care-flow
description: Thêm loại flow chăm sóc khách hàng hoặc bước mới trong flow (chờ, điều kiện, gửi tin, gắn nhãn, báo nhân viên). Dùng khi làm tính năng chăm sóc chủ động.
---

# Thêm flow chăm sóc (Giai đoạn 2)

1. Flow = trigger → [condition] → action → wait → ... lưu dạng JSON (jsonb), validate bằng FluentValidation.
2. Trước mỗi lần gửi: kiểm tra đồng ý nhận tin, khung giờ, tần suất tối đa, điều kiện dừng (đã mua, đã phản hồi, đã hủy).
3. Chọn kênh: còn trong khung → tin tư vấn; ngoài khung → ZNS (nếu có mẫu phù hợp) → không gửi được thì bỏ qua và ghi log.
4. Khách trả lời giữa flow → chuyển về luồng hội thoại bình thường, flow tạm dừng.
5. Mọi lần gửi ghi vào `flow_runs` để báo cáo và làm bằng chứng tuân thủ.
6. Mỗi bước flow là job Hangfire theo tenant (skill `tenant-safe-feature` bước 5); idempotent: chạy lại cùng job không gửi trùng tin.
