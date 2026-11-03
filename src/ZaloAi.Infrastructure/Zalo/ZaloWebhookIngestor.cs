using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Zalo;

public enum WebhookOutcome
{
    /// <summary>Chữ ký sai / không đúng app → 401, không xử lý gì.</summary>
    Rejected,

    /// <summary>Hợp lệ nhưng không cần làm gì (sự kiện khác, OA chưa kết nối, tin trùng, tin của chính bot dội về) → 200.</summary>
    Ignored,

    /// <summary>Đã lưu tin và xếp job → 200.</summary>
    Accepted,
}

/// <summary>
/// Nhận webhook Zalo (CLAUDE.md mục 3, 8; docs/zalo-api-notes.md mục 4–5): xác thực chữ ký → đúng app → chống trùng (Redis + unique DB)
/// → tìm OA thuộc tenant nào → lưu tin (mã hóa) → xếp job → trả nhanh (Zalo chỉ chờ 2 giây). Không gọi AI ở đây.
/// Tenant chỉ suy ra từ OA ID đã kết nối (sau khi chữ ký hợp lệ), không từ dữ liệu khác của khách.
/// </summary>
public sealed partial class ZaloWebhookIngestor(
    IOptions<ZaloOptions> options,
    IDistributedStore store,
    ChannelConnectionLookup lookup,
    TenantContext tenantContext,
    ConversationRepository conversations,
    IFieldEncryptor encryptor,
    IBackgroundJobClient jobs,
    IInboxNotifier notifier,
    TimeProvider time,
    ILogger<ZaloWebhookIngestor> logger)
{
    /// <summary>Zalo gửi lại tối đa ~1 giờ sau → nhớ msg_id đã nhận 1 ngày là đủ (DB unique là lớp thứ hai).</summary>
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromDays(1);

    public async Task<WebhookOutcome> HandleAsync(string rawBody, string? signature, CancellationToken cancellationToken)
    {
        var zalo = options.Value;
        if (string.IsNullOrEmpty(zalo.AppId) || string.IsNullOrEmpty(zalo.WebhookSecret))
        {
            LogRejected(logger, "Zalo:AppId/WebhookSecret chưa cấu hình");
            return WebhookOutcome.Rejected;
        }

        var e = ZaloWebhookEvent.Parse(rawBody);
        if (e is null || e.AppId != zalo.AppId
            || !ZaloWebhookSignature.IsValid(signature, zalo.AppId, rawBody, e.Timestamp, zalo.WebhookSecret))
        {
            // Chỉ ghi định dạng header (không ghi giá trị) để đối chiếu với Zalo thật khi chữ ký chưa khớp.
            LogRejected(logger, e is null ? "body không đọc được" : $"chữ ký/app không khớp, header {ZaloWebhookSignature.DescribeFormat(signature)}");
            return WebhookOutcome.Rejected;
        }

        if (!(e.IsUserMessage || (e.IsOaMessage && e.SenderAdminId is not null)) || e.OaId is null || e.UserId is null || e.MessageId is null)
        {
            // Sự kiện khác (quan tâm, đã đọc...) hoặc tin bot tự gửi qua API dội về (oa_send_* không có admin_id).
            LogIgnored(logger, e.EventName);
            return WebhookOutcome.Ignored;
        }

        if (!await store.SetIfNotExistsAsync($"zalo:event:{e.OaId}:{e.MessageId}", "1", DedupeTtl, cancellationToken))
        {
            return WebhookOutcome.Ignored;
        }

        var route = await lookup.FindAsync(ChannelKind.Zalo, e.OaId, cancellationToken);
        if (route is not { Status: ConnectionStatus.Active, TenantActive: true })
        {
            LogIgnored(logger, e.EventName);
            return WebhookOutcome.Ignored;
        }

        tenantContext.Set(route.TenantId, userId: null, role: null, isSuperAdmin: false);
        return e.IsUserMessage
            ? await SaveCustomerMessageAsync(route, e, cancellationToken)
            : await SaveStaffMessageAsync(route, e, cancellationToken);
    }

    private async Task<WebhookOutcome> SaveCustomerMessageAsync(ConnectionRoute route, ZaloWebhookEvent e, CancellationToken cancellationToken)
    {
        var tenantId = route.TenantId;
        var conversation = await GetConversationAsync(route, e.UserId!, cancellationToken);
        var message = conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.In,
            Sender = MessageSender.Customer,
            ContentEnc = encryptor.Encrypt(ContentOf(e)),
            ExternalMessageId = e.MessageId,
        });
        conversation.LastCustomerMessageAt = conversation.LastMessageAt = time.GetUtcNow();
        if (conversation.Mode == ConversationMode.Human)
        {
            conversation.NeedsAttentionSince ??= time.GetUtcNow(); // nhân viên đang xử lý: khách chờ người trả lời
        }

        if (!await TrySaveAsync(cancellationToken))
        {
            return WebhookOutcome.Ignored;
        }

        var messageId = message.Id;
        jobs.Enqueue<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, messageId, CancellationToken.None));
        await notifier.PublishAsync(tenantId, new InboxEvent(conversation.Mode == ConversationMode.Human ? "attention" : "message", conversation.Id), cancellationToken);
        return WebhookOutcome.Accepted;
    }

    /// <summary>Nhân viên trả lời thẳng trong công cụ chat của OA: lưu tin, chuyển hội thoại sang nhân viên để bot không chen vào.</summary>
    private async Task<WebhookOutcome> SaveStaffMessageAsync(ConnectionRoute route, ZaloWebhookEvent e, CancellationToken cancellationToken)
    {
        var conversation = await GetConversationAsync(route, e.UserId!, cancellationToken);
        conversations.AddMessage(route.TenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.Out,
            Sender = MessageSender.Staff,
            ContentEnc = encryptor.Encrypt(ContentOf(e)),
            ExternalMessageId = e.MessageId,
            DeliveryStatus = DeliveryStatus.Sent,
        });
        conversation.Mode = ConversationMode.Human;
        conversation.HandoffReason = "staff_replied_in_oa";
        conversation.NeedsAttentionSince = null; // nhân viên đã trả lời
        conversation.LastReminderAt = null;
        conversation.LastMessageAt = time.GetUtcNow();

        if (!await TrySaveAsync(cancellationToken))
        {
            return WebhookOutcome.Ignored;
        }

        await notifier.PublishAsync(route.TenantId, new InboxEvent("message", conversation.Id), cancellationToken);
        return WebhookOutcome.Accepted;
    }

    private async Task<Conversation> GetConversationAsync(ConnectionRoute route, string userId, CancellationToken cancellationToken)
    {
        var contact = await conversations.GetOrAddContactAsync(route.TenantId, ChannelKind.Zalo, userId, displayName: null, cancellationToken);
        return await conversations.FindOpenConversationAsync(route.TenantId, contact.Id, route.ConnectionId, cancellationToken)
            ?? conversations.AddConversation(route.TenantId, new Conversation
            {
                ContactId = contact.Id,
                Channel = ChannelKind.Zalo,
                ConnectionId = route.ConnectionId,
            });
    }

    /// <summary>Unique (tenant, external_message_id) / (tenant, channel, user) chặn trùng khi 2 request đến cùng lúc.</summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await conversations.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    private static string ContentOf(ZaloWebhookEvent e)
    {
        var text = e.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            return text.Length > 4000 ? text[..4000] : text;
        }

        var attachment = e.Attachments.Count > 0 ? e.Attachments[0] : null;
        return attachment is null
            ? InboundText.ForAttachment("file")!
            : InboundText.ForAttachment(attachment.Type) ?? attachment.Url ?? InboundText.ForAttachment("file")!;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook Zalo bị từ chối: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Webhook Zalo bỏ qua: {EventName}")]
    private static partial void LogIgnored(ILogger logger, string eventName);
}
