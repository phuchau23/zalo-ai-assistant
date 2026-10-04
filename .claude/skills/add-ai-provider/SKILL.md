---
name: add-ai-provider
description: Thêm hoặc đổi nhà cung cấp/model AI (hiện chỉ Gemini; model chat, embedding, provider mới). Dùng khi đổi model chat/embedding, cập nhật giá, hoặc thêm provider mới.
---

# Thêm / đổi nhà cung cấp AI

Hiện tại: **chỉ Gemini** (chủ dự án chốt 2026-10-04, bỏ Claude). Thêm provider khác → hỏi chủ dự án trước.

1. Interface ở Core, tách chat và embedding (cấu hình độc lập):
   - `IChatProvider` (`src/ZaloAi.Core/Ai/IChatProvider.cs`): `ChatAsync(ChatRequest(System, Turns, JsonSchema?, Temperature, MaxOutputTokens))` → `ChatResult(Text, Provider, Model, InputTokens, OutputTokens, CostUsd, LatencyMs)`. Output tokens gồm cả token "suy nghĩ".
   - `IEmbeddingProvider` (`src/ZaloAi.Core/Ai/IEmbeddingProvider.cs`): tài liệu và câu hỏi có tiền tố nhiệm vụ riêng.
   - Implementation trong `src/ZaloAi.Ai/Providers/`, đăng ký trong `src/ZaloAi.Ai/DependencyInjection.cs` (chọn theo `Ai:ChatProvider` / `Ai:EmbedProvider`; `fake` cho test).
2. **Kiểm tra docs chính thức** (và một request thật bằng script, không in key) trước khi viết request/response; ghi ngày kiểm chứng trong comment class.
3. Model, key, giá đọc từ `AiOptions` (`src/ZaloAi.Core/Options/AiOptions.cs`, section `Ai`), không hardcode. Key chỉ qua env/user-secrets. Giá ở `Ai:Pricing` trong appsettings (USD/1 triệu token), cập nhật tay theo trang giá.
4. HTTP qua typed `HttpClient` + `AddStandardResilienceHandler` (timeout, retry, circuit breaker). Model chính quá tải (503) → model dự phòng `Ai:ChatFallbackModel`.
5. Map lỗi về exception chung trong `src/ZaloAi.Core/Errors/AiProviderException.cs`: `AiRateLimitedException` (429), `AiOverloadedException` (503), `AiTimeoutException`, `AiProviderException`. Message không chứa nội dung phản hồi, prompt hay key. `BotEngine` bắt lỗi này → câu dự phòng + chuyển người.
6. Không log prompt/nội dung tin; nội dung gửi AI đã được `PiiMasker` che trước.
7. Test với `HttpMessageHandler` giả (xem `tests/ZaloAi.UnitTests/Ai/GeminiChatProviderTests.cs`): định dạng request, key ở header, tách phần "thought", chi phí, fallback, lỗi không lộ nội dung.
8. Chạy eval trước khi đổi model ở production: `dotnet run --project src/ZaloAi.Api -- eval [--delay-ms N] [--out file.json]` — phải đạt ngưỡng (thường ≥ 85%, 0 bịa giá, an toàn 100%).
9. Đổi model embedding = tạo lại toàn bộ vector (skill `db-migration` nếu đổi số chiều).
10. Nhắc chủ dự án: production chỉ dùng gói **trả phí** (gói Gemini miễn phí cho Google dùng dữ liệu để cải thiện sản phẩm); provider là bên nhận dữ liệu ở nước ngoài → cập nhật hồ sơ pháp lý.
