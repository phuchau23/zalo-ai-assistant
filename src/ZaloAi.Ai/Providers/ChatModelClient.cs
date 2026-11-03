using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Một nhà cung cấp chat gọi đúng một model — một bước trong chuỗi dự phòng của <see cref="FallbackChatProvider"/>.
/// Lỗi ném <see cref="AiProviderException"/>; <see cref="AiProviderException.CanFallback"/> quyết định có chuyển bước tiếp theo không.
/// </summary>
public interface IChatModelClient
{
    string ProviderName { get; }

    Task<ChatResult> CallAsync(string model, ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Gửi request tới nhà cung cấp AI và đổi lỗi mạng/HTTP thành lỗi chung. Message không chứa nội dung phản hồi, prompt hay key.</summary>
internal static class AiHttp
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(message, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiTimeoutException(ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            throw new AiTimeoutException(ex);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            throw new AiOverloadedException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiUnavailableException(ex);
        }
    }

    /// <summary>
    /// 429 → hết lượt (kèm retry-after); 503, 498 (Groq hết chỗ) → quá tải; 413 → quá dài; 400/401/403/404 → từ chối (không chuyển);
    /// 5xx khác → lỗi máy chủ. Thành công → không làm gì.
    /// </summary>
    public static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        throw status switch
        {
            429 => new AiRateLimitedException(RetryAfter(response)),
            503 or 498 => new AiOverloadedException(),
            413 => new AiRequestTooLargeException(),
            400 or 401 or 403 or 404 => new AiRequestRejectedException(status),
            >= 500 => new AiUnavailableException(),
            _ => new AiProviderException($"Dịch vụ AI trả lỗi HTTP {status}."),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
