namespace ZaloAi.Core.Errors;

/// <summary>
/// Lỗi khi gọi nhà cung cấp AI (Gemini, Groq). Message an toàn để hiện (không chứa key, không chứa nội dung khách).
/// Job Hangfire gặp lỗi này sẽ tự thử lại; API trả 503 "ai_unavailable".
/// </summary>
public class AiProviderException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>
    /// true = lỗi tạm thời / hết lượt / kết quả hỏng: chuyển sang model hoặc nhà cung cấp tiếp theo.
    /// false = request sai, key sai, nội dung bị chặn: không chuyển (chuyển cũng sai tiếp), phải sửa cấu hình.
    /// </summary>
    public virtual bool CanFallback => false;
}

/// <summary>Vượt hạn mức gọi (HTTP 429). Nên đợi rồi thử lại.</summary>
/// <param name="retryAfter">Thời gian nhà cung cấp bảo đợi (header retry-after), nếu có.</param>
public sealed class AiRateLimitedException(TimeSpan? retryAfter = null, Exception? innerException = null)
    : AiProviderException("Dịch vụ AI đang quá tải hoặc vượt hạn mức, vui lòng thử lại sau.", innerException)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public override bool CanFallback => true;
}

/// <summary>Quá thời gian chờ phản hồi.</summary>
public sealed class AiTimeoutException(Exception? innerException = null)
    : AiProviderException("Dịch vụ AI phản hồi quá chậm, vui lòng thử lại.", innerException)
{
    public override bool CanFallback => true;
}

/// <summary>Model đang quá tải (HTTP 503) — thử model dự phòng hoặc thử lại sau.</summary>
public sealed class AiOverloadedException(Exception? innerException = null)
    : AiProviderException("Dịch vụ AI đang quá tải, vui lòng thử lại sau.", innerException)
{
    public override bool CanFallback => true;
}

/// <summary>Không kết nối được hoặc máy chủ AI lỗi (HTTP 5xx khác 503, lỗi mạng).</summary>
public sealed class AiUnavailableException(Exception? innerException = null)
    : AiProviderException("Không kết nối được dịch vụ AI hoặc dịch vụ đang lỗi, vui lòng thử lại sau.", innerException)
{
    public override bool CanFallback => true;
}

/// <summary>Yêu cầu dài quá giới hạn của model (HTTP 413, ví dụ vượt số token/phút của gói Groq) — model khác có thể nhận.</summary>
public sealed class AiRequestTooLargeException()
    : AiProviderException("Yêu cầu quá dài so với giới hạn của model AI.")
{
    public override bool CanFallback => true;
}

/// <summary>Model trả kết quả không dùng được (rỗng, bị cắt, JSON sai cấu trúc) — model khác có thể làm đúng.</summary>
public sealed class AiInvalidOutputException(string? reason = null)
    : AiProviderException(reason is null ? "Dịch vụ AI trả kết quả sai định dạng." : $"Dịch vụ AI trả kết quả không dùng được (lý do: {reason}).")
{
    public override bool CanFallback => true;
}

/// <summary>
/// Nhà cung cấp từ chối yêu cầu (HTTP 400/401/403/404): sai key, sai tên model, request sai cấu trúc. KHÔNG chuyển nhà cung cấp
/// khác — phải sửa cấu hình (log mức Error).
/// </summary>
public sealed class AiRequestRejectedException(int statusCode)
    : AiProviderException($"Dịch vụ AI từ chối yêu cầu (HTTP {statusCode}) — kiểm tra key, tên model và cấu hình AI.")
{
    public int StatusCode { get; } = statusCode;
}
