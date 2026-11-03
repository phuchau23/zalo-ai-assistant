---
name: add-ai-provider
description: Thêm hoặc đổi nhà cung cấp/model AI (chat: Gemini + Groq dự phòng; embedding: chỉ Gemini; model chat, embedding, provider mới, thứ tự dự phòng). Dùng khi đổi model chat/embedding, cập nhật giá, sửa chuỗi dự phòng, hoặc thêm provider mới.
---

# Thêm / đổi nhà cung cấp AI

Hiện tại: chat **Gemini, Groq làm dự phòng** khi Gemini hết lượt/lỗi (chủ dự án chốt 2026-11-03); embedding **chỉ Gemini**. Thêm provider khác → hỏi chủ dự án trước.

1. Interface ở Core, tách chat và embedding (cấu hình độc lập):
   - `IChatProvider` (`src/ZaloAi.Core/Ai/IChatProvider.cs`): `ChatAsync(ChatRequest(System, Turns, JsonSchema?, Temperature, MaxOutputTokens))` → `ChatResult(Text, Provider, Model, InputTokens, OutputTokens, CostUsd, LatencyMs)`. Output tokens gồm cả token "suy nghĩ". Code nghiệp vụ chỉ dùng interface này, không đọc cấu trúc riêng của nhà cung cấp.
   - `IEmbeddingProvider` (`src/ZaloAi.Core/Ai/IEmbeddingProvider.cs`): tài liệu và câu hỏi có tiền tố nhiệm vụ riêng.
   - Mỗi nhà cung cấp chat là một `IChatModelClient` (`src/ZaloAi.Ai/Providers/ChatModelClient.cs`) gọi **đúng một model**: `GeminiChatProvider`, `GroqChatProvider`. `FallbackChatProvider` là `IChatProvider` thật, chạy chuỗi bước theo `Ai:ChatProviders` (mặc định `gemini,groq`: `ChatModel` → `ChatFallbackModel` → `GroqModels` theo thứ tự). Đăng ký trong `src/ZaloAi.Ai/DependencyInjection.cs` (`ChatSteps`); `Ai:ChatProvider=fake` cho test.
2. **Kiểm tra docs chính thức** (và một request thật bằng script hoặc `eval --ids`, không in key) trước khi viết request/response; ghi ngày kiểm chứng trong comment class.
3. Model, key, giá, thứ tự đọc từ `AiOptions` (`src/ZaloAi.Core/Options/AiOptions.cs`, section `Ai`), không hardcode. Key chỉ qua env/user-secrets (API và Worker dùng chung `zaloai-dev`). Giá ở `Ai:Pricing` trong appsettings của **cả Api và Worker** (USD/1 triệu token), cập nhật tay theo trang giá.
4. HTTP qua typed `HttpClient` + `AddStandardResilienceHandler` (timeout, retry 5xx, circuit breaker; không retry 429 và timeout) và `AiHttp.SendAsync` / `AiHttp.ThrowIfFailed` để map lỗi. Đổi model: đo độ trễ thật trước (model "suy nghĩ" mặc định có thể mất hàng chục giây).
5. Lỗi chung trong `src/ZaloAi.Core/Errors/AiProviderException.cs`, `CanFallback` quyết định có sang bước tiếp:
   - Chuyển: `AiRateLimitedException` (429, có `RetryAfter`), `AiOverloadedException` (503, Groq 498), `AiUnavailableException` (5xx khác, lỗi mạng), `AiTimeoutException`, `AiRequestTooLargeException` (413), `AiInvalidOutputException` (rỗng/bị cắt/JSON hỏng, Groq 400 `json_validate_failed`, 422).
   - Không chuyển: `AiRequestRejectedException` (400/401/403/404 — log Error), `AiProviderException` thường (vd Gemini chặn SAFETY).
   - Message không chứa nội dung phản hồi, prompt hay key. `BotEngine` bắt `AiProviderException` → câu dự phòng + chuyển người.
6. Request có JSON schema: `FallbackChatProvider` kiểm tra kết quả bằng `JsonOutput.Validate` cho mọi nhà cung cấp (sửa nhẹ → hỏi lại 1 lần kèm nhắc định dạng → sang bước tiếp). Schema dự án chỉ dùng type/enum/properties/required/items — dùng thêm từ khóa khác thì bổ sung `JsonOutput`. Groq strict cần mọi trường nằm trong `required` (test `Real_project_schemas_get_additional_properties_false_on_every_object`).
7. Không log prompt/nội dung tin; nội dung gửi AI đã được `PiiMasker` che trước. Không cắt prompt cho vừa giới hạn (mất quy tắc an toàn → bịa): để nhà cung cấp trả 413 rồi chuyển bước.
8. Test với `HttpMessageHandler` giả (`tests/ZaloAi.UnitTests/Ai/AiHttpFakes.cs`, `GeminiChatProviderTests`, `GroqChatProviderTests`, `FallbackChatProviderTests`, `GroqCallSiteTests`): định dạng request, key ở header, tách phần "thought", chi phí, chuỗi dự phòng, lỗi không lộ nội dung, mọi chỗ gọi AI chạy đúng với câu trả lời của nhà cung cấp mới.
9. Chạy eval trước khi đổi model ở production: `dotnet run --project src/ZaloAi.Api -- eval [--ids N01,S07] [--delay-ms N] [--out file.json]` — phải đạt ngưỡng (thường ≥ 85%, 0 bịa giá, an toàn 100%). Eval riêng một nhà cung cấp: env `Ai__ChatProviders=groq`.
10. Đổi model embedding = tạo lại toàn bộ vector (skill `db-migration` nếu đổi số chiều).
11. Nhắc chủ dự án: production chỉ dùng Gemini gói **trả phí** (gói miễn phí cho Google dùng dữ liệu để cải thiện sản phẩm); Groq bật Zero Data Retention; mỗi provider là bên nhận dữ liệu ở nước ngoài → cập nhật hồ sơ pháp lý.
