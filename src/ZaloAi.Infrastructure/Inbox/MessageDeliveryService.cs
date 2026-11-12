using Hangfire;
using Microsoft.Extensions.Logging;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Inbox;

/// <summary>
/// Tạo và gửi tin ra kênh (bot, nhân viên, câu hệ thống) — một chỗ duy nhất ghi trạng thái gửi.
/// Tin lưu "pending" trước khi gửi: job chạy lại gửi lại đúng tin đó. Lỗi tạm → ném (Hangfire thử lại); lỗi vĩnh viễn → failed.
/// Kênh chặn mọi tin (vd OA chưa mua gói Zalo) khi bot đang trả lời → chuyển nhân viên + báo Telegram, để khách không bị bỏ rơi.
/// </summary>
public sealed partial class MessageDeliveryService(
    ConversationRepository conversations,
    IEnumerable<IChannelAdapter> channels,
    IFieldEncryptor encryptor,
    IInboxNotifier notifier,
    IBackgroundJobClient jobs,
    TimeProvider time,
    ILogger<MessageDeliveryService> logger)
{
    /// <summary>Lý do chuyển người khi kênh chặn gửi (FE + Telegram có nhãn riêng).</summary>
    public const string ChannelBlockedReason = "channel_blocked";

    /// <summary>Thêm tin ra (chưa lưu, chưa gửi). Cập nhật thời điểm tin cuối của hội thoại.</summary>
    public Message AddOutgoing(
        Guid tenantId,
        Conversation conversation,
        MessageSender sender,
        string text,
        Guid? senderUserId = null,
        Guid? replyToMessageId = null,
        string? traceJson = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.LastMessageAt = time.GetUtcNow();
        return conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.Out,
            Sender = sender,
            ContentEnc = encryptor.Encrypt(text),
            SenderUserId = senderUserId,
            ReplyToMessageId = replyToMessageId,
            AiTraceJson = traceJson,
            DeliveryStatus = DeliveryStatus.Pending,
        });
    }

    /// <summary>Gửi một tin đã lưu (pending) rồi ghi trạng thái và báo hộp thư.</summary>
    public async Task DeliverAsync(Guid tenantId, Conversation conversation, Contact contact, Message message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(message);
        if (message.DeliveryStatus != DeliveryStatus.Pending)
        {
            return;
        }

        var channel = channels.FirstOrDefault(c => c.Channel == conversation.Channel)
            ?? throw new InvalidOperationException($"Chưa có adapter cho kênh {conversation.Channel}.");
        var handedOff = false;
        try
        {
            var sent = await channel.SendAsync(
                new OutgoingMessage(tenantId, conversation.Id, message.Id, contact.ExternalUserId, encryptor.Decrypt(message.ContentEnc), conversation.ConnectionId),
                cancellationToken);
            message.DeliveryStatus = DeliveryStatus.Sent;
            message.ExternalMessageId ??= sent.ExternalMessageId;
            message.DeliveryError = null;
        }
        catch (ChannelPermanentException ex)
        {
            message.DeliveryStatus = DeliveryStatus.Failed;
            message.DeliveryError = ex.Code;
            LogDeliveryFailed(logger, message.Id, ex.Code);

            // Bot không gửi được cho BẤT KỲ ai qua kênh này → nhân viên trả lời tay (vd trong app Zalo OA). Chỉ chuyển một lần:
            // hội thoại đã ở chế độ nhân viên (bot vừa chuyển người, nhân viên đang xử lý) thì đã có người lo, không báo trùng.
            if (ex.ChannelBlocked && conversation.Mode == ConversationMode.Bot)
            {
                conversation.Mode = ConversationMode.Human;
                conversation.HandoffReason = ChannelBlockedReason;
                conversation.NeedsAttentionSince ??= time.GetUtcNow();
                handedOff = true;
            }
        }

        await conversations.SaveChangesAsync(cancellationToken);
        await notifier.PublishAsync(tenantId, new InboxEvent("message", conversation.Id), cancellationToken);
        if (handedOff)
        {
            var conversationId = conversation.Id;
            jobs.Enqueue<AttentionNotifyJob>(job => job.RunAsync(tenantId, conversationId, AttentionNotifyJob.Handoff, CancellationToken.None));
            await notifier.PublishAsync(tenantId, new InboxEvent("attention", conversationId), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không gửi được tin {MessageId}: {Code}")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid messageId, string code);
}
