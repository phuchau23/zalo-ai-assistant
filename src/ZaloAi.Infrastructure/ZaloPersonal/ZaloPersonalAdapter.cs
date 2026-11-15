using System.Globalization;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>
/// Gửi tin qua nick Zalo cá nhân (sidecar, CONTRACT.md mục 5). Tin của bot: báo "đang soạn" rồi chờ một lúc như người thật gõ;
/// tin nhân viên gửi ngay. Nick không hoạt động → lỗi vĩnh viễn (không thử lại vô ích). Sidecar/mạng lỗi → lỗi tạm (job thử lại;
/// sidecar chống gửi trùng theo messageId). Zalo từ chối → lỗi vĩnh viễn.
/// </summary>
public sealed class ZaloPersonalAdapter(
    SidecarClient sidecar,
    ChannelConnectionRepository connections,
    ZaloPersonalRepository personal,
    PersonalSendLimiter limiter,
    IDistributedStore store,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time) : IChannelAdapter
{
    public ChannelKind Channel => ChannelKind.ZaloPersonal;

    public async Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ConnectionId is not { } connectionId
            || await connections.GetAsync(message.TenantId, connectionId, cancellationToken) is not { Status: ConnectionStatus.Active }
            || await personal.GetAccountAsync(message.TenantId, connectionId, cancellationToken) is not { } account)
        {
            throw new ChannelPermanentException("zp:disconnected", "Nick Zalo cá nhân đã ngắt hoặc đang mất kết nối.");
        }

        await ThrottleAsync(connectionId, cancellationToken);

        // Nhịp gửi theo giờ/ngày (mục 7): câu trả lời của bot vượt mức → chuyển nhân viên trả lời tay (channelBlocked → handoff).
        // Tin chủ động đã kiểm lượt trước khi xếp; tin nhân viên chỉ được đếm, không chặn người thật.
        var limits = PersonalPacing.Effective(account, options.Value, time.GetUtcNow());
        var (day, hour) = await limiter.CountAsync(connectionId, PacingCounter.Messages, cancellationToken);
        if (message.Sender == MessageSender.Bot && !message.Proactive && (day > limits.MessagesPerDay || hour > limits.MessagesPerHour))
        {
            throw new ChannelPermanentException("zp:send_limit", "Nick đã gửi đủ số tin cho phép.", channelBlocked: true);
        }

        var typingMs = message.Sender == MessageSender.Bot ? TypingDelayMs(message.Text, options.Value, Random.Shared) : 0;
        if (typingMs > 0 && PersonalPacing.IsNight(options.Value, time.GetUtcNow()))
        {
            // Ban đêm người thật gõ chậm hơn (sidecar kẹp tối đa 15 giây).
            typingMs = (int)Math.Min(typingMs * options.Value.NightTypingFactor, 15_000);
        }
        var replyTo = message is { Sender: MessageSender.Bot, Proactive: false } ? message.AnswersExternalMessageId : null;
        var result = await sidecar.SendAsync(connectionId, message.MessageId, message.ExternalUserId, message.Text, typingMs, cancellationToken, replyTo);
        if (result.Superseded)
        {
            throw new MessageSupersededException(); // khách nhắn tiếp trong lúc "đang soạn" → câu trả lời cho tin mới thay thế
        }

        if (result.Rejection is { } rejection)
        {
            throw new ChannelPermanentException($"zp:{rejection.Code ?? "rejected"}", "Zalo từ chối tin.");
        }

        return new SendResult(result.MessageId);
    }

    /// <summary>
    /// Thời gian "đang soạn" trước khi gửi: ~2 giây + 40 ms mỗi ký tự, lệch ngẫu nhiên ±20%, kẹp trong [ReplyDelayMinMs, ReplyDelayMaxMs].
    /// </summary>
    public static int TypingDelayMs(string? text, ZaloPersonalOptions o, Random random)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(random);
        var baseMs = 2000 + (40 * (text?.Length ?? 0));
        var jittered = baseMs * (0.8 + (random.NextDouble() * 0.4));
        return (int)Math.Clamp(jittered, o.ReplyDelayMinMs, o.ReplyDelayMaxMs);
    }

    /// <summary>Giới hạn số tin mỗi phút qua một nick — vượt thì để job gửi lại phút sau.</summary>
    private async Task ThrottleAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        var key = string.Create(CultureInfo.InvariantCulture, $"zp:rate:{connectionId:N}:{minute}");
        if (await store.IncrementAsync(key, TimeSpan.FromMinutes(2), cancellationToken) > options.Value.SendPerMinute)
        {
            throw new ChannelUnavailableException("Vượt giới hạn gửi tin của nick trong phút này, thử lại sau.");
        }
    }
}
