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

        services.AddSingleton<FakeEmbeddingProvider>();
        services.AddTransient<IEmbeddingProvider>(sp =>
            sp.GetRequiredService<IOptions<AiOptions>>().Value.EmbedProvider == "fake"
                ? sp.GetRequiredService<FakeEmbeddingProvider>()
                : sp.GetRequiredService<GeminiEmbeddingProvider>());

        services.AddSingleton<FakeChatProvider>();
        services.AddTransient<IChatProvider>(sp =>
            sp.GetRequiredService<IOptions<AiOptions>>().Value.ChatProvider == "fake"
                ? sp.GetRequiredService<FakeChatProvider>()
                : sp.GetRequiredService<GeminiChatProvider>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IBotEngine, BotEngine>();
        services.AddScoped<ICareAdvisor, CareAdvisor>();

        return services;
    }

    private static void ConfigureGemini(IServiceProvider sp, HttpClient http)
    {
        var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
        http.BaseAddress = new Uri(options.GeminiBaseUrl);
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
        // GeminiChatProvider chuyển sang model dự phòng. Các lỗi tạm thời khác (503, 5xx, timeout) vẫn thử lại.
        resilience.Retry.ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests
            && HttpClientResiliencePredicates.IsTransient(args.Outcome));
    }
}
