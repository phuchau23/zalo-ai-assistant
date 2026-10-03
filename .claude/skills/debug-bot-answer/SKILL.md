---
name: debug-bot-answer
description: Điều tra vì sao bot trả lời sai, bịa, hoặc không trả lời. Dùng khi chủ dự án hoặc khách báo "bot nói sai giá", "bot trả lời lạ", "bot không trả lời".
---

# Điều tra câu trả lời sai của bot

1. Tìm tin nhắn trong `messages` (theo tenant, thời gian). Lấy `ai_trace` gồm: chunk ids đã dùng, prompt version, model, output JSON, lý do handoff.
   - Truy vấn luôn kèm `tenant_id`. Nội dung tin đã mã hóa: chỉ giải mã khi thật cần, không chép nội dung khách vào log/issue/chat.
   - Bot không trả lời: xem job trên dashboard `/hangfire` (Failed? đang retry?) và lỗi trên Sentry trước.
2. Kiểm tra theo thứ tự:
   a. Dữ liệu có thông tin đúng không? (tài liệu cũ, chưa ingest xong, ingest lỗi)
   b. Search có lấy đúng chunk không? (chạy lại search với câu hỏi, xem điểm similarity)
   c. Prompt có hướng dẫn đúng không?
   d. Model có làm theo không?
3. Sửa đúng tầng gây lỗi. Thêm câu hỏi đó vào bộ eval để không tái diễn.
4. Không tắt quy tắc "không biết thì chuyển người" để sửa lỗi.
