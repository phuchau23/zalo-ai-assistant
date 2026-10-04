using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Realtime;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Gửi một tin ra (nhân viên / câu hệ thống) đã lưu pending. Chạy lại an toàn: tin đã gửi thì bỏ qua.</summary>
[Queue(JobQueues.Messages)]
public sealed class SendOutgoingMessageJob(TenantContext tenantContext, ConversationRepository conversations, MessageDeliveryService delivery)
{
    public async Task RunAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var message = await conversations.GetMessageAsync(tenantId, messageId, cancellationToken);
        if (message is not { Direction: MessageDirection.Out, DeliveryStatus: DeliveryStatus.Pending })
        {
            return;
        }

        var conversation = await conversations.GetConversationAsync(tenantId, message.ConversationId, cancellationToken);
        var contact = conversation is null ? null : await conversations.GetContactAsync(tenantId, conversation.ContactId, cancellationToken);
        if (conversation is null || contact is null)
        {
            return;
        }

        await delivery.DeliverAsync(tenantId, conversation, contact, message, cancellationToken);
    }
}

/// <summary>
/// Báo nhóm nhân viên trên Telegram: bot chuyển người / khẩn cấp / khách chờ lâu. Chỉ gửi loại sự việc + link hộp thư,
/// không gửi nội dung tin hay thông tin cá nhân của khách.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class AttentionNotifyJob(
    TenantContext tenantContext,
    ConversationRepository conversations,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    TelegramClient telegram,
    IOptions<AppOptions> app)
{
    public const string Handoff = "handoff";
    public const string Reminder = "reminder";

    /// <summary>AI chăm sóc chủ động thấy khách cần NGƯỜI liên hệ (phàn nàn, sức khỏe, cần thông tin nhân viên mới có...).</summary>
    public const string Care = "care";

    public async Task RunAsync(Guid tenantId, Guid conversationId, string kind, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.TelegramChatId) || conversation is null || tenant is null)
        {
            return;
        }

        var urgent = conversation.Urgency == Urgency.Urgent;
        var head = kind == Reminder
            ? $"⏰ Khách đã chờ quá {settings.ReminderMinutes} phút chưa ai trả lời"
            : kind == Care ? "💬 Có khách cần nhân viên chủ động liên hệ (trang Cần chăm sóc)"
            : urgent ? "🚨 KHẨN CẤP: khách có dấu hiệu nguy hiểm" : "🙋 Khách cần nhân viên hỗ trợ";
        var reason = kind != Care && conversation.HandoffReason is { } r ? $"\nLý do: {ReasonLabel(r)}" : "";
        var link = kind == Care
            ? $"Mở trang Cần chăm sóc: {app.Value.AdminUrl.TrimEnd('/')}/care"
            : $"Mở hộp thư: {app.Value.AdminUrl.TrimEnd('/')}/inbox?c={conversationId}";
        var test = conversation.IsTest ? "[Chat thử] " : ""; // để DN thử cấu hình Telegram bằng Chat thử
        await telegram.SendAsync(settings.TelegramChatId, $"{test}{head} — {tenant.Name}{reason}\n{link}", cancellationToken);
    }

    private static string ReasonLabel(string reason) => reason switch
    {
        "customer_request" => "khách muốn gặp nhân viên",
        "no_knowledge" or "low_confidence" => "bot chưa có thông tin để trả lời",
        "complaint" or "negative_sentiment" => "khách không hài lòng",
        "booking" => "khách muốn đặt lịch",
        "urgent" => "dấu hiệu nguy hiểm",
        "medical" => "câu hỏi chuyên môn sức khỏe",
        "media" => "khách gửi hình ảnh/tệp",
        _ => "bot cần người hỗ trợ",
    };
}

/// <summary>
/// Quét mỗi phút (job HỆ THỐNG, mọi tenant): ca đang chờ người quá số phút cài đặt mà chưa nhắc → nhắc (hộp thư + Telegram),
/// lặp lại sau mỗi khoảng đó cho tới khi có người trả lời. IgnoreQueryFilters có chủ đích; chỉ đọc id + mốc thời gian.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class AttentionReminderSweepJob(AppDbContext db, IBackgroundJobClient jobs, TimeProvider time)
{
    public const string RecurringId = "inbox-attention-reminder";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var earliest = now.AddMinutes(-5); // ReminderMinutes nhỏ nhất cho phép
        var candidates = await (
            from c in db.Conversations.IgnoreQueryFilters()
            join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
            join s in db.HandoffSettings.IgnoreQueryFilters() on c.TenantId equals s.TenantId into settings
            from s in settings.DefaultIfEmpty()
            where c.NeedsAttentionSince != null && c.NeedsAttentionSince < earliest
                  && c.Mode == ConversationMode.Human && !c.IsTest && t.Status == TenantStatus.Active
            select new { c.Id, c.TenantId, c.NeedsAttentionSince, c.LastReminderAt, Minutes = s == null ? 10 : s.ReminderMinutes })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var c in candidates)
        {
            var due = (c.LastReminderAt ?? c.NeedsAttentionSince!.Value).AddMinutes(c.Minutes);
            if (due <= now)
            {
                jobs.Enqueue<AttentionReminderJob>(job => job.RunAsync(c.TenantId, c.Id, CancellationToken.None));
            }
        }
    }
}

/// <summary>Nhắc một ca chờ lâu trong đúng tenant: ghi mốc nhắc, báo hộp thư, gửi Telegram.</summary>
[Queue(JobQueues.Default)]
public sealed class AttentionReminderJob(
    TenantContext tenantContext,
    ConversationRepository conversations,
    IInboxNotifier notifier,
    IBackgroundJobClient jobs,
    TimeProvider time)
{
    public async Task RunAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken);
        if (conversation?.NeedsAttentionSince is null)
        {
            return; // đã có người trả lời trong lúc chờ
        }

        conversation.LastReminderAt = time.GetUtcNow();
        await conversations.SaveChangesAsync(cancellationToken);
        await notifier.PublishAsync(tenantId, new InboxEvent("reminder", conversationId), cancellationToken);
        jobs.Enqueue<AttentionNotifyJob>(job => job.RunAsync(tenantId, conversationId, AttentionNotifyJob.Reminder, CancellationToken.None));
    }
}
