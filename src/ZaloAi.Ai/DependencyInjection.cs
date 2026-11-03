using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ZaloAi.Ai.Bot;
using ZaloAi.Ai.Care;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai;

public static class DependencyInjection
{
    /// <summary>Đăng ký nhà cung cấp AI theo cấu hình (AiOptions đã được đăng ký và kiểm tra ở Infrastructure).</summary>
    public static IServiceCollection AddZaloAiAi(this IServiceCollection services)
    {
        services.AddHttpClient<GeminiEmbeddingProvider>(ConfigureGemini)
            .AddStandardResilienceHandler()
            .Configure(ConfigureResilience);
        services.AddHttpClient<GeminiChatProvider>(ConfigureGemini)
            .AddStandardResilienceHandler()
            .Configure(ConfigureResilience);
        services.AddHttpClient<GroqChatProvider>(ConfigureGroq)
            .AddStandardResilienceHandler()
            .Configure(ConfigureResilience);

        services.AddSingleton<FakeEmbeddingProvider>();
        services.AddTransient<IEmbeddingProvider>(sp =>
            sp.GetRequiredService<IOptions<AiOptions>>().Value.EmbedProvider == "fake"
                ? sp.GetRequiredService<FakeEmbeddingProvider>()
                : sp.GetRequiredService<GeminiEmbeddingProvider>());

        services.AddSingleton<FakeChatProvider>();
        services.AddSingleton<ChatCooldowns>();
        services.AddTransient(sp => new FallbackChatProvider(
            ChatSteps(sp),
            sp.GetRequiredService<ChatCooldowns>(),
            sp.GetRequiredService<IOptions<AiOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FallbackChatProvider>>()));
        services.AddTransient<IChatProvider>(sp =>
            sp.GetRequiredService<IOptions<AiOptions>>().Value.ChatProvider == "fake"
                ? sp.GetRequiredService<FakeChatProvider>()
                : sp.GetRequiredService<FallbackChatProvider>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IBotEngine, BotEngine>();
        services.AddScoped<ICareAdvisor, CareAdvisor>();
        services.AddScoped<IAppointmentBriefWriter, Care.AppointmentBriefWriter>();
        services.AddScoped<IReportQuestionInterpreter, Reports.ReportQuestionInterpreter>();

        return services;
    }

    /// <summary>Chuỗi dự phòng theo Ai:ChatProviders: gemini = model chính → model dự phòng; groq = Ai:GroqModels (chỉ khi có key).</summary>
    private static List<ChatStep> ChatSteps(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        var steps = new List<ChatStep>();
        foreach (var provider in options.ChatProviderOrder)
        {
            if (provider == "gemini")
            {
                var gemini = sp.GetRequiredService<GeminiChatProvider>();
                steps.Add(new ChatStep(gemini, options.ChatModel));
                if (!string.IsNullOrWhiteSpace(options.ChatFallbackModel) && options.ChatFallbackModel != options.ChatModel)
                {
                    steps.Add(new ChatStep(gemini, options.ChatFallbackModel));
                }
            }
            else if (provider == "groq" && options.GroqEnabled)
            {
                var groq = sp.GetRequiredService<GroqChatProvider>();
                steps.AddRange(options.GroqModelList.Select(model => new ChatStep(groq, model)));
            }
        }

        return steps;
    }

    private static void ConfigureGemini(IServiceProvider sp, HttpClient http)
    {
        var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        http.BaseAddress = new Uri(options.GeminiBaseUrl);
        http.Timeout = Timeout.InfiniteTimeSpan; // thời gian chờ do resilience handler quản lý
    }

    private static void ConfigureGroq(IServiceProvider sp, HttpClient http)
    {
        var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        http.BaseAddress = new Uri(options.GroqBaseUrl);
        http.Timeout = Timeout.InfiniteTimeSpan; // thời gian chờ do resilience handler quản lý
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions resilience, IServiceProvider sp)
    {
        var seconds = sp.GetRequiredService<IOptions<AiOptions>>().Value.RequestTimeoutSeconds;
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(seconds);
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(seconds * 3);
        resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(seconds * 2);
        resilience.Retry.MaxRetryAttempts = 2;

        // 429 (vượt hạn mức, ví dụ gói free hết quota ngày) thử lại vài giây sau cũng vô ích: trả lỗi ngay để
        // FallbackChatProvider chuyển sang model / nhà cung cấp dự phòng (retry-after ngắn thì nó tự chờ). Quá thời gian chờ cũng vậy: model đang "suy nghĩ" quá lâu thì
        // chờ thêm 2 lượt chỉ làm khách đợi tới 90 giây. Các lỗi tạm thời khác (503, 5xx) vẫn thử lại.
        resilience.Retry.ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests
            && args.Outcome.Exception is not Polly.Timeout.TimeoutRejectedException
            && HttpClientResiliencePredicates.IsTransient(args.Outcome));
    }
}
