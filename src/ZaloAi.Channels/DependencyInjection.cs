using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using ZaloAi.Channels.Webchat;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Options;

namespace ZaloAi.Channels;

public static class DependencyInjection
{
    /// <summary>Đăng ký mọi kênh; worker chọn adapter theo <see cref="IChannelAdapter.Channel"/>.</summary>
    public static IServiceCollection AddZaloAiChannels(this IServiceCollection services)
    {
        services.AddSingleton<IChannelAdapter, WebchatAdapter>();

        services.AddHttpClient<ZaloClient>((sp, http) =>
            {
                http.Timeout = Timeout.InfiniteTimeSpan; // thời gian chờ do resilience handler quản lý
            })
            .AddStandardResilienceHandler()
            .Configure((HttpStandardResilienceOptions resilience, IServiceProvider sp) =>
            {
                var seconds = sp.GetRequiredService<IOptions<ZaloOptions>>().Value.RequestTimeoutSeconds;
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(seconds);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(seconds * 2);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(seconds * 2);

                // Mọi API Zalo dùng đều là POST không idempotent: làm mới token (refresh token chỉ dùng 1 lần) và gửi tin
                // (gửi lại = khách nhận 2 lần). Không tự thử lại ở tầng HTTP; job tự thử lại sau khi kiểm tra trạng thái.
                resilience.Retry.DisableForUnsafeHttpMethods();
            });

        return services;
    }
}
