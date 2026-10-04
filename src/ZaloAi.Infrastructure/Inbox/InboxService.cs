using Hangfire;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Realtime;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Inbox;

/// <summary>
/// Thao tác hộp thư (docs/FEATURE-SPECS.md mục 1; CLAUDE.md M5): bot chuyển người, nhân viên tiếp quản / trả lời / trả lại bot, gán người.
/// Tin ra kênh lưu pending rồi giao job gửi (API trả nhanh, lỗi mạng có thử lại). Mọi thay đổi báo hộp thư realtime.
/// </summary>
public sealed class InboxService(
    ConversationRepository conversations,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    MessageDeliveryService delivery,
    AuditLogRepository audit,
    IInboxNotifier notifier,
    IBackgroundJobClient jobs,
    TimeProvider time)
{
    /// <summary>Khách đang chờ người (bot vừa chuyển, hoặc khách nhắn khi nhân viên đang xử lý). Giữ mốc cũ nếu đã chờ từ trước.</summary>
    public void MarkNeedsAttention(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.NeedsAttentionSince ??= time.GetUtcNow();
    }

    /// <summary>
    /// Sau khi bot trả lời và chuyển người: gửi câu chuyển tiếp (trong/ngoài giờ) trừ ca khẩn cấp (câu khẩn cấp đã báo đủ),
    /// đánh dấu chờ người, thông báo Telegram. Gọi trong job, sau khi đã lưu tin bot.
    /// </summary>
    public async Task<Message?> AfterBotHandoffAsync(Guid tenantId, Conversation conversation, Urgency urgency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        MarkNeedsAttention(conversation);
        Message? notice = null;
        if (urgency != Urgency.Urgent)
        {
            var tenant = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
            var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
            notice = delivery.AddOutgoing(tenantId, conversation, MessageSender.System, HandoffTexts.Handoff(settings, tenant.Name, time.GetUtcNow()));
        }

        await conversations.SaveChangesAsync(cancellationToken);
        var conversationId = conversation.Id;
        jobs.Enqueue<AttentionNotifyJob>(job => job.RunAsync(tenantId, conversationId, AttentionNotifyJob.Handoff, CancellationToken.None));
        await notifier.PublishAsync(tenantId, new InboxEvent("attention", conversation.Id), cancellationToken);
        return notice;
    }

    /// <summary>Nhân viên tiếp quản: bot im, gán cho nhân viên, gửi câu giới thiệu (nếu bật).</summary>
    public async Task TakeOverAsync(Guid tenantId, Guid conversationId, Guid userId, string userName, CancellationToken cancellationToken)
    {
        var conversation = await RequireAsync(tenantId, conversationId, cancellationToken);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();

        conversation.Mode = ConversationMode.Human;
        conversation.AssignedUserId = userId;
        conversation.HandoffReason ??= "staff_takeover";
        Message? intro = null;
        if (settings.TakeoverMessageEnabled)
        {
            intro = delivery.AddOutgoing(tenantId, conversation, MessageSender.System,
                HandoffTexts.Fill(settings.TakeoverMessage, tenant.Name, userName, settings, time.GetUtcNow()), senderUserId: userId);
            conversation.NeedsAttentionSince = null; // khách đã được phản hồi
            conversation.LastReminderAt = null;
        }

        audit.Add(tenantId, userId, "conversation.taken_over", conversationId.ToString());
        await conversations.SaveChangesAsync(cancellationToken);
        EnqueueSend(tenantId, intro);
        await notifier.PublishAsync(tenantId, new InboxEvent("updated", conversationId), cancellationToken);
    }

    /// <summary>Trả lại cho bot: bot trả lời tin tiếp theo của khách; gửi câu "trợ lý AI luôn sẵn sàng" nếu bật.</summary>
    public async Task ReturnToBotAsync(Guid tenantId, Guid conversationId, Guid? userId, CancellationToken cancellationToken)
    {
        var conversation = await RequireAsync(tenantId, conversationId, cancellationToken);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();

        conversation.Mode = ConversationMode.Bot;
        conversation.HandoffReason = null;
        conversation.Urgency = Urgency.None;
        conversation.NeedsAttentionSince = null;
        conversation.LastReminderAt = null;
        Message? notice = null;
        if (settings.ReturnToBotMessageEnabled && !conversation.IsTest)
        {
            notice = delivery.AddOutgoing(tenantId, conversation, MessageSender.System,
                HandoffTexts.Fill(settings.ReturnToBotMessage, tenant.Name, null, settings, time.GetUtcNow()));
        }

        audit.Add(tenantId, userId, "conversation.returned_to_bot", conversationId.ToString());
        await conversations.SaveChangesAsync(cancellationToken);
        EnqueueSend(tenantId, notice);
        await notifier.PublishAsync(tenantId, new InboxEvent("updated", conversationId), cancellationToken);
    }

    /// <summary>Nhân viên nhắn khách: tự tiếp quản (bot im), gán cho mình nếu chưa có người, kèm chữ ký (nếu bật).</summary>
    public async Task<Message> SendStaffMessageAsync(Guid tenantId, Guid conversationId, Guid userId, string userName, string text, CancellationToken cancellationToken)
    {
        var conversation = await RequireAsync(tenantId, conversationId, cancellationToken);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);

        conversation.Mode = ConversationMode.Human;
        conversation.AssignedUserId ??= userId;
        conversation.HandoffReason ??= "staff_takeover";
        conversation.NeedsAttentionSince = null;
        conversation.LastReminderAt = null;
        var content = settings.StaffSignatureEnabled ? HandoffTexts.Sign(text, userName) : text;
        var message = delivery.AddOutgoing(tenantId, conversation, MessageSender.Staff, content, senderUserId: userId);

        await conversations.SaveChangesAsync(cancellationToken);
        EnqueueSend(tenantId, message);
        await notifier.PublishAsync(tenantId, new InboxEvent("message", conversationId), cancellationToken);
        return message;
    }

    public async Task AssignAsync(Guid tenantId, Guid conversationId, Guid? assigneeId, Guid? byUserId, CancellationToken cancellationToken)
    {
        var conversation = await RequireAsync(tenantId, conversationId, cancellationToken);
        conversation.AssignedUserId = assigneeId;
        audit.Add(tenantId, byUserId, "conversation.assigned", $"{conversationId}:{assigneeId}");
        await conversations.SaveChangesAsync(cancellationToken);
        await notifier.PublishAsync(tenantId, new InboxEvent("updated", conversationId), cancellationToken);
    }

    private async Task<Conversation> RequireAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken) ?? throw new NotFoundException();

    private void EnqueueSend(Guid tenantId, Message? message)
    {
        if (message is null)
        {
            return;
        }

        var messageId = message.Id;
        jobs.Enqueue<SendOutgoingMessageJob>(job => job.RunAsync(tenantId, messageId, CancellationToken.None));
    }
}
