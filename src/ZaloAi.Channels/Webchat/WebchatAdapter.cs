using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;

namespace ZaloAi.Channels.Webchat;

/// <summary>
/// Kênh chat trong trang quản trị ("Chat thử", sau này chat trên website). Tin trả lời đã nằm trong database,
/// trình duyệt tự lấy về (polling / SSE ở M5) → gửi không cần làm gì thêm.
/// </summary>
public sealed class WebchatAdapter : IChannelAdapter
{
    public ChannelKind Channel => ChannelKind.Webchat;

    public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken) =>
        Task.FromResult(new SendResult(null));
}
