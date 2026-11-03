using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>Một bước trong chuỗi dự phòng: nhà cung cấp + model.</summary>
public sealed record ChatStep(IChatModelClient Client, string Model)
{
    public string Key => $"{Client.ProviderName}/{Model}";
}

/// <summary>Model vừa hết lượt / quá tải → bỏ qua tới thời điểm này (bộ nhớ của từng process, mất khi khởi động lại là chấp nhận được).</summary>
public sealed class ChatCooldowns
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    public bool IsCooling(string key, DateTimeOffset now) => _until.TryGetValue(key, out var until) && until > now;

    public void Cool(string key, DateTimeOffset until) => _until[key] = until;
}

/// <summary>
/// <see cref="IChatProvider"/> duy nhất code nghiệp vụ dùng: thử lần lượt các bước (Ai:ChatProviders — Gemini model chính → Gemini dự phòng →
/// Groq model chính → Groq dự phòng). Kết quả chuẩn hóa <see cref="ChatResult"/> giống nhau mọi nhà cung cấp, nên code phía sau không cần biết
/// ai đã trả lời (Provider/Model chỉ để ghi chi phí và "Vì sao bot trả lời vậy?").
/// - Lỗi tạm thời / hết lượt / quá dài / JSON hỏng (<see cref="AiProviderException.CanFallback"/>) → bước tiếp theo. 400/401/403/404 → dừng,
///   log Error để sửa cấu hình (bot vẫn trả câu dự phòng + chuyển người, không bỏ rơi khách).
/// - 429 kèm retry-after ngắn (≤ Ai:ChatMaxRetryAfterSeconds) → chờ rồi thử lại cùng model một lần. 429/503 → bỏ qua model đó một lúc.
/// - Request có JSON schema: kiểm tra kết quả theo schema cho MỌI nhà cung cấp (<see cref="JsonOutput"/>); sai → sửa nhẹ → thử lại 1 lần kèm
///   nhắc định dạng → vẫn sai thì coi như bước đó lỗi.
/// - Không cắt prompt khi quá dài (có thể mất quy tắc an toàn/dữ liệu → bịa): nhà cung cấp trả 413 thì sang bước khác.
/// - Token/chi phí của các lượt hỏng được cộng vào kết quả cuối (không mất chi phí khi ghi usage_records).
/// Log chỉ có nhà cung cấp, model, loại lỗi — không key, không nội dung.
/// </summary>
public sealed partial class FallbackChatProvider(
    IReadOnlyList<ChatStep> steps,
    ChatCooldowns cooldowns,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<FallbackChatProvider> logger) : IChatProvider
{
    private const string FormatReminder =
        "\n\n[Hệ thống] Lần trả lời trước không đúng định dạng. Chỉ trả về đúng MỘT object JSON hợp lệ theo schema, đủ mọi trường bắt buộc, " +
        "không thêm chữ, không dùng ```.";

    private readonly AiOptions _options = options.Value;

    public async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (steps.Count == 0)
        {
            throw new AiProviderException("Chưa cấu hình nhà cung cấp AI nào.");
        }

        // Mọi bước đều đang nghỉ (hết lượt cả) → vẫn thử hết theo thứ tự, không từ chối khách khi chưa thử.
        var now = time.GetUtcNow();
        var active = steps.Where(s => !cooldowns.IsCooling(s.Key, now)).ToList();
        if (active.Count == 0)
        {
            active = steps.ToList();
        }

        var spent = new Spent();
        AiProviderException? last = null;
        for (var i = 0; i < active.Count; i++)
        {
            var step = active[i];
            try
            {
                var result = await CallStepAsync(step, request, spent, cancellationToken);
                if (step != steps[0])
                {
                    LogAnsweredByFallback(logger, step.Client.ProviderName, step.Model);
                }

                return spent.AddTo(result);
            }
            catch (AiProviderException ex) when (ex.CanFallback)
            {
                last = ex;
                if (ex is AiRateLimitedException or AiOverloadedException && _options.ChatCooldownSeconds > 0)
                {
                    var wait = TimeSpan.FromSeconds(_options.ChatCooldownSeconds);
                    if (ex is AiRateLimitedException { RetryAfter: { } retryAfter } && retryAfter > wait)
                    {
                        wait = retryAfter;
                    }

                    cooldowns.Cool(step.Key, time.GetUtcNow() + wait);
                }

                var next = i + 1 < active.Count ? active[i + 1] : null;
                LogFallback(logger, step.Client.ProviderName, step.Model, ex.GetType().Name, ex.Message, next?.Client.ProviderName ?? "(hết)", next?.Model ?? "");
            }
            catch (AiRequestRejectedException ex)
            {
                LogRejected(logger, step.Client.ProviderName, step.Model, ex.StatusCode);
                throw;
            }
        }

        LogAllFailed(logger, active.Count, last?.GetType().Name ?? "");
        throw new AiProviderException("Tất cả dịch vụ AI đều đang lỗi hoặc hết lượt, vui lòng thử lại sau.", last);
    }

    private async Task<ChatResult> CallStepAsync(ChatStep step, ChatRequest request, Spent spent, CancellationToken cancellationToken)
    {
        var attempt = request;
        var waited = false;
        var reminded = false;
        while (true)
        {
            ChatResult result;
            try
            {
                result = await step.Client.CallAsync(step.Model, attempt, cancellationToken);
            }
            catch (AiRateLimitedException ex) when (!waited
                                                    && ex.RetryAfter is { } retryAfter
                                                    && retryAfter <= TimeSpan.FromSeconds(_options.ChatMaxRetryAfterSeconds))
            {
                waited = true;
                var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                LogWaiting(logger, step.Client.ProviderName, step.Model, seconds);
                await Task.Delay(retryAfter, time, cancellationToken);
                continue;
            }

            if (request.JsonSchema is null)
            {
                return result;
            }

            if (JsonOutput.Validate(result.Text, request.JsonSchema) is { } json)
            {
                return json == result.Text ? result : result with { Text = json };
            }

            spent.Add(result);
            if (reminded)
            {
                throw new AiInvalidOutputException();
            }

            reminded = true;
            LogInvalidOutput(logger, step.Client.ProviderName, step.Model);
            attempt = request with { System = request.System + FormatReminder, Temperature = 0 };
        }
    }

    /// <summary>Token, chi phí, thời gian của các lượt trả kết quả hỏng — cộng vào kết quả cuối.</summary>
    private sealed class Spent
    {
        private int _input;
        private int _output;
        private decimal _cost;
        private long _latency;

        public void Add(ChatResult result)
        {
            _input += result.InputTokens;
            _output += result.OutputTokens;
            _cost += result.CostUsd;
            _latency += result.LatencyMs;
        }

        public ChatResult AddTo(ChatResult result) => result with
        {
            InputTokens = result.InputTokens + _input,
            OutputTokens = result.OutputTokens + _output,
            CostUsd = result.CostUsd + _cost,
            LatencyMs = result.LatencyMs + _latency,
        };
    }

    /// <param name="reason">Message của lỗi chung — an toàn để log (không có key, không có nội dung khách), ví dụ lý do "json_validate_failed".</param>
    [LoggerMessage(Level = LogLevel.Warning, Message = "AI {Provider}/{Model} lỗi ({ErrorType}: {Reason}), chuyển sang {NextProvider}/{NextModel}")]
    private static partial void LogFallback(ILogger logger, string provider, string model, string errorType, string reason, string nextProvider, string nextModel);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI trả lời bằng bước dự phòng {Provider}/{Model}")]
    private static partial void LogAnsweredByFallback(ILogger logger, string provider, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI {Provider}/{Model} hết lượt tạm thời, chờ {Seconds} giây rồi thử lại")]
    private static partial void LogWaiting(ILogger logger, string provider, string model, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI {Provider}/{Model} trả JSON sai định dạng, thử lại 1 lần kèm nhắc định dạng")]
    private static partial void LogInvalidOutput(ILogger logger, string provider, string model);

    [LoggerMessage(Level = LogLevel.Error, Message = "AI {Provider}/{Model} từ chối yêu cầu (HTTP {StatusCode}): kiểm tra key, tên model, cấu hình — không chuyển nhà cung cấp khác")]
    private static partial void LogRejected(ILogger logger, string provider, string model, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cả {Count} bước AI đều lỗi (lỗi cuối: {ErrorType})")]
    private static partial void LogAllFailed(ILogger logger, int count, string errorType);
}
