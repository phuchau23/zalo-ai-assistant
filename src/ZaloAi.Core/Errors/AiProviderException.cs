namespace ZaloAi.Core.Errors;

/// <summary>
/// Lỗi khi gọi nhà cung cấp AI (Gemini, Claude...). Message an toàn để hiện (không chứa key, không chứa nội dung khách).
/// Job Hangfire gặp lỗi này sẽ tự thử lại; API trả 503 "ai_unavailable".
/// </summary>
public class AiProviderException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Vượt hạn mức gọi (HTTP 429). Nên đợi rồi thử lại.</summary>
public sealed class AiRateLimitedException(Exception? innerException = null)
    : AiProviderException("Dịch vụ AI đang quá tải hoặc vượt hạn mức, vui lòng thử lại sau.", innerException);

/// <summary>Quá thời gian chờ phản hồi.</summary>
public sealed class AiTimeoutException(Exception? innerException = null)
    : AiProviderException("Dịch vụ AI phản hồi quá chậm, vui lòng thử lại.", innerException);
