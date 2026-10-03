using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai;

public static class DependencyInjection
{
    /// <summary>Đăng ký nhà cung cấp AI theo cấu hình (AiOptions đã được đăng ký và kiểm tra ở Infrastructure).</summary>
    public static IServiceCollection AddZaloAiAi(this IServiceCollection services)
    {
        services.AddHttpClient<GeminiEmbeddingProvider>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<AiOptions>>().Value;
                http.BaseAddress = new Uri(options.GeminiBaseUrl);
                http.Timeout = Timeout.InfiniteTimeSpan; // thời gian chờ do resilience handler quản lý
            })
            .AddStandardResilienceHandler()
            .Configure((HttpStandardResilienceOptions resilience, IServiceProvider sp) =>
            {
                var seconds = sp.GetRequiredService<IOptions<AiOptions>>().Value.RequestTimeoutSeconds;
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(seconds);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(seconds * 3);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(seconds * 2);
                resilience.Retry.MaxRetryAttempts = 2;
            });

        services.AddSingleton<FakeEmbeddingProvider>();
        services.AddTransient<IEmbeddingProvider>(sp =>
            sp.GetRequiredService<IOptions<AiOptions>>().Value.EmbedProvider == "fake"
                ? sp.GetRequiredService<FakeEmbeddingProvider>()
                : sp.GetRequiredService<GeminiEmbeddingProvider>());

        return services;
    }
}
