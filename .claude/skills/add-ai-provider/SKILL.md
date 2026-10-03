---
name: add-ai-provider
description: Thêm hoặc đổi nhà cung cấp AI (Gemini, Claude, model khác). Dùng khi đổi model chat/embedding hoặc thêm provider mới.
---

# Thêm / đổi nhà cung cấp AI

1. Implement `IAiProvider` trong `src/ZaloAi.Ai/Providers/`:
   `Task<ChatResult> ChatAsync(ChatRequest req, CancellationToken ct)` — req: `System, Messages, JsonSchema?, MaxTokens, Timeout`; result: `Text, Json?, Usage(InputTokens, OutputTokens), CostUsd`
   `Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)`
2. Đọc model và key từ config (`AiOptions` trong `src/ZaloAi.Core/Options/AiOptions.cs`, section `Ai`), không hardcode. Key chỉ đặt qua env/user-secrets.
3. Gọi HTTP qua `HttpClientFactory` + `Microsoft.Extensions.Http.Resilience` (timeout, retry, circuit breaker).
4. Map lỗi của provider về exception chung: `AiRateLimitedException`, `AiTimeoutException`, `AiProviderException` để job Hangfire retry đúng.
5. Không log prompt/nội dung tin gốc; message lỗi trả về từ provider có thể chứa nội dung khách → che bằng `SensitiveDataRedactor.MaskText` trước khi log (Sentry đã có `SentryScrubber`).
6. Tính chi phí từ bảng giá trong config (cập nhật thủ công theo trang giá của provider).
7. Chạy bộ eval nhỏ (`src/ZaloAi.Ai/Evals/`) so sánh chất lượng trả lời tiếng Việt với provider cũ trước khi đổi ở production.
8. Nhắc chủ dự án: production chỉ dùng gói trả phí có cam kết không dùng dữ liệu để huấn luyện; provider mới là bên nhận dữ liệu ở nước ngoài → cập nhật hồ sơ pháp lý.
