using System.Globalization;
using Microsoft.Extensions.Options;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;

namespace ZaloAi.Infrastructure.Zalo;

/// <summary>
/// Gửi tin tư vấn qua Zalo OA (docs/zalo-api-notes.md mục 6–7). Lỗi tạm (mạng, -32, -200) → ném để job thử lại;
/// lỗi vĩnh viễn (khách không còn nhận được, OA mất quyền) → <see cref="ChannelPermanentException"/>, job đánh dấu Failed.
/// Token hỏng (-216/-220) → làm mới (có khóa) rồi gửi lại đúng 1 lần.
/// </summary>
public sealed class ZaloAdapter(
    ZaloClient zalo,
    ZaloConnectionService connections,
    IDistributedStore store,
    TimeProvider time,
    IOptions<ZaloOptions> options) : IChannelAdapter
{
    public ChannelKind Channel => ChannelKind.Zalo;

    public async Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ConnectionId is not { } connectionId)
        {
            throw new ChannelPermanentException("zalo:no_connection", "Hội thoại không còn gắn với OA nào (đã ngắt kết nối).");
        }

        var token = await connections.GetAccessTokenAsync(message.TenantId, connectionId, forceRefresh: false, cancellationToken)
            ?? throw new ChannelPermanentException("zalo:needs_reauth", "Kết nối Zalo cần cấp quyền lại.");

        string? firstId = null;
        foreach (var part in ZaloClient.SplitText(message.Text))
        {
            await ThrottleAsync(connectionId, cancellationToken);
            ZaloSendResult result;
            try
            {
                result = await zalo.SendTextAsync(token, message.ExternalUserId, part, cancellationToken);
            }
            catch (ZaloApiException ex) when (ex.IsTokenError)
            {
                token = await connections.GetAccessTokenAsync(message.TenantId, connectionId, forceRefresh: true, cancellationToken)
                    ?? throw new ChannelPermanentException("zalo:needs_reauth", "Kết nối Zalo cần cấp quyền lại.");
                result = await SendOnceAsync(message, connectionId, token, part, cancellationToken);
            }
            catch (ZaloApiException ex)
            {
                throw await ClassifyAsync(message, connectionId, ex, cancellationToken);
            }

            firstId ??= result.MessageId;
        }

        return new SendResult(firstId);
    }

    private async Task<ZaloSendResult> SendOnceAsync(OutgoingMessage message, Guid connectionId, string token, string text, CancellationToken cancellationToken)
    {
        try
        {
            return await zalo.SendTextAsync(token, message.ExternalUserId, text, cancellationToken);
        }
        catch (ZaloApiException ex)
        {
            throw await ClassifyAsync(message, connectionId, ex, cancellationToken);
        }
    }

    private async Task<Exception> ClassifyAsync(OutgoingMessage message, Guid connectionId, ZaloApiException ex, CancellationToken cancellationToken)
    {
        if (ex.IsAuthorizationError || ex.IsTokenError)
        {
            await connections.MarkNeedsReauthAsync(message.TenantId, connectionId, ex.ShortCode, cancellationToken);
            return new ChannelPermanentException(ex.ShortCode, "Kết nối Zalo mất quyền gửi tin.");
        }

        if (ex.Code == -200)
        {
            return new ZaloTransientException("Zalo gửi tin thất bại (-200), thử lại sau.", ex);
        }

        // Khách không nhận được (-230 quá 7 ngày, -227 khóa...) hoặc tham số sai: gửi lại vô ích.
        return new ChannelPermanentException(ex.ShortCode, ex.IsRecipientError ? "Khách hiện không nhận được tin từ OA." : "Zalo từ chối tin.");
    }

    /// <summary>Giới hạn số tin gửi mỗi phút qua một OA (Zalo giới hạn theo OA tùy gói) — vượt thì để job gửi lại phút sau.</summary>
    private async Task ThrottleAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
        var key = string.Create(CultureInfo.InvariantCulture, $"zalo:rate:{connectionId:N}:{minute}");
        var count = await store.IncrementAsync(key, TimeSpan.FromMinutes(2), cancellationToken);
        if (count > options.Value.SendPerMinutePerOa)
        {
            throw new ZaloTransientException("Vượt giới hạn gửi tin của OA trong phút này, thử lại sau.");
        }
    }
}
