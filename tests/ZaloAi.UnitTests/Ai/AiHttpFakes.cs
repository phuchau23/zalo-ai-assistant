using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Ai;

/// <summary>HTTP giả cho Gemini/Groq: ghi lại request, trả phản hồi theo kịch bản (không gọi mạng thật).</summary>
internal sealed class FakeAiHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return respond(request, body);
    }
}

internal static class AiFakes
{
    public const string GeminiMain = "gemini-main";
    public const string GeminiLite = "gemini-lite";
    public const string GroqMain = "openai/gpt-oss-120b";
    public const string GroqBackup = "openai/gpt-oss-20b";

    public static AiOptions Options(Action<AiOptions>? configure = null)
    {
        var options = new AiOptions
        {
            ChatModel = GeminiMain,
            ChatFallbackModel = GeminiLite,
            GeminiApiKey = "gemini-test-key",
            GroqApiKey = "groq-test-key",
            GroqModels = $"{GroqMain},{GroqBackup}",
            ChatMaxRetryAfterSeconds = 10,
            Pricing = new(StringComparer.OrdinalIgnoreCase)
            {
                [GeminiMain] = new ModelPrice { InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m },
                [GroqMain] = new ModelPrice { InputPerMillionUsd = 0.15m, OutputPerMillionUsd = 0.60m },
                [GroqBackup] = new ModelPrice { InputPerMillionUsd = 0.075m, OutputPerMillionUsd = 0.30m },
            },
        };
        configure?.Invoke(options);
        return options;
    }

    public static HttpResponseMessage GeminiOk(string text, int prompt = 100, int output = 20) => Json(new
    {
        candidates = new[]
        {
            new
            {
                content = new { role = "model", parts = new object[] { new { text = "đang nghĩ...", thought = true }, new { text } } },
                finishReason = "STOP",
            },
        },
        usageMetadata = new { promptTokenCount = prompt, candidatesTokenCount = output, thoughtsTokenCount = 0 },
        modelVersion = (string?)null,
    });

    public static HttpResponseMessage GroqOk(string text, string model = GroqMain, int prompt = 100, int output = 20) => Json(new
    {
        id = "chatcmpl-1",
        @object = "chat.completion",
        model,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
        usage = new { prompt_tokens = prompt, completion_tokens = output, total_tokens = prompt + output },
    });

    public static HttpResponseMessage Status(HttpStatusCode status, int? retryAfterSeconds = null, string? body = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") };
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    /// <summary>Model trong request: Gemini nằm trong URL, Groq nằm trong body.</summary>
    public static string ModelOf(HttpRequestMessage request, string body)
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains(":generateContent", StringComparison.Ordinal))
        {
            return url.Split("models/")[1].Split(':')[0];
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
    }

    public static GeminiChatProvider Gemini(FakeAiHandler handler, AiOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(options.GeminiBaseUrl) }, Microsoft.Extensions.Options.Options.Create(options));

    public static GroqChatProvider Groq(FakeAiHandler handler, AiOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(options.GroqBaseUrl) }, Microsoft.Extensions.Options.Options.Create(options));

    /// <summary>
    /// Chuỗi đầy đủ như production: Gemini chính → Gemini dự phòng → Groq chính → Groq dự phòng, mỗi model trả lời theo <paramref name="respond"/>
    /// (tham số: tên model, body request).
    /// </summary>
    public static (FallbackChatProvider Chat, FakeAiHandler Handler, ChatCooldowns Cooldowns) Chain(
        Func<string, string, HttpResponseMessage> respond,
        AiOptions? options = null,
        ChatCooldowns? cooldowns = null)
    {
        options ??= Options();
        var handler = new FakeAiHandler((request, body) => respond(ModelOf(request, body), body));
        var gemini = Gemini(handler, options);
        var groq = Groq(handler, options);
        ChatStep[] steps =
        [
            new(gemini, GeminiMain),
            new(gemini, GeminiLite),
            new(groq, GroqMain),
            new(groq, GroqBackup),
        ];
        cooldowns ??= new ChatCooldowns();
        var chat = new FallbackChatProvider(
            steps, cooldowns, Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System, NullLogger<FallbackChatProvider>.Instance);
        return (chat, handler, cooldowns);
    }

    /// <summary>Gemini hết lượt cả 2 model, Groq trả lời bằng <paramref name="groqText"/>.</summary>
    public static (FallbackChatProvider Chat, FakeAiHandler Handler) GeminiOutGroqAnswers(Func<string, string> groqText)
    {
        var (chat, handler, _) = Chain((model, body) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? Status(HttpStatusCode.TooManyRequests)
            : GroqOk(groqText(body), model));
        return (chat, handler);
    }

    public static IReadOnlyList<string> Models(this FakeAiHandler handler) =>
        handler.Calls.Select(c => ModelOf(c.Request, c.Body)).ToList();

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    internal static ChatRequest Request(string? schema = null) => new(
        "Bạn là trợ lý.",
        [new ChatTurn(ChatRole.User, "Giá massage?"), new ChatTurn(ChatRole.Assistant, "Dạ 450k ạ"), new ChatTurn(ChatRole.User, "Cảm ơn")],
        schema);
}
