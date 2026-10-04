using Microsoft.Extensions.DependencyInjection;
using ZaloAi.Channels.Webchat;
using ZaloAi.Core.Channels;

namespace ZaloAi.Channels;

public static class DependencyInjection
{
    /// <summary>Đăng ký mọi kênh; worker chọn adapter theo <see cref="IChannelAdapter.Channel"/>.</summary>
    public static IServiceCollection AddZaloAiChannels(this IServiceCollection services)
    {
        services.AddSingleton<IChannelAdapter, WebchatAdapter>();
        return services;
    }
}
