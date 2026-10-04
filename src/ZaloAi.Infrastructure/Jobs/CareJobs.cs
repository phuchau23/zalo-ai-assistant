using Hangfire;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Chăm sóc chủ động (docs/FEATURE-SPECS.md mục 2, quyết định 2026-10-05): AI đọc lại khách → bot TỰ NHẮN (qua các giới hạn cứng
/// của <see cref="CareDecision"/>), hoặc gọi nhân viên (gợi ý + Telegram), hoặc thôi. Mỗi khách tối đa 1 gợi ý đang mở.
/// Lỗi AI → Hangfire thử lại theo chính sách chung.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class CareAnalysisJob(
    TenantContext tenantContext,
    ConversationRepository conversations,
    ContactRepository contacts,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    ICareAdvisor advisor,
    IFieldEncryptor encryptor,
    IInboxNotifier notifier,
    IBackgroundJobClient jobs,
    TimeProvider time)
{
    /// <summary>Zalo chỉ cho OA nhắn tư vấn người dùng có tương tác trong 7 ngày (docs/zalo-api-notes.md).</summary>
    public static readonly TimeSpan ZaloMessagingWindow = TimeSpan.FromDays(7);

    private const int RecentMessages = 30;

    /// <summary>Hội thoại "nguội" (job quét) hoặc nhân viên bấm "AI chăm sóc khách này".</summary>
    public async Task AnalyzeConversationAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken);
        if (conversation is null || conversation.NeedsAttentionSince is not null)
        {
            return; // khách đang chờ người: hộp thư đã báo
        }

        await AnalyzeAsync(tenantId, conversation, followUp: null, cancellationToken);
    }

    /// <summary>Tới giờ hẹn chăm sóc lại trong ghi chú của nhân viên.</summary>
    public async Task FollowUpAsync(Guid tenantId, Guid noteId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var note = await contacts.GetNoteAsync(tenantId, noteId, cancellationToken);
        if (note is null || note.FollowUpQueuedAt is not null)
        {
            return;
        }

        var conversation = await contacts.LatestConversationAsync(tenantId, note.ContactId, cancellationToken);
        note.FollowUpQueuedAt = time.GetUtcNow();
        if (conversation is null)
        {
            await contacts.SaveChangesAsync(cancellationToken);
            return;
        }

        await AnalyzeAsync(tenantId, conversation, note, cancellationToken);
    }

    private async Task AnalyzeAsync(Guid tenantId, Conversation conversation, ContactNote? followUp, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetAsync(tenantId, conversation.ContactId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (contact is null || tenant is null)
        {
            return;
        }

        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var messages = await conversations.ListMessagesAsync(tenantId, conversation.Id, conversation.SummarizedUntil, cancellationToken);
        var notes = await contacts.ListNotesAsync(tenantId, contact.Id, 10, cancellationToken);
        var result = await advisor.AnalyzeAsync(
            new CareAnalysisRequest(
                tenantId,
                new BotProfile(tenant.Name, tenant.IndustrySlug, tenant.BotName, tenant.BotPronoun, tenant.BotTone, null, tenant.PrivacyUrl),
                contact.DisplayName,
                conversation.SummaryEnc is null ? null : encryptor.Decrypt(conversation.SummaryEnc),
                messages.TakeLast(RecentMessages).Select(m => new BotHistoryTurn(m.Sender, encryptor.Decrypt(m.ContentEnc))).ToList(),
                notes.Reverse().Select(ToInput).ToList(),
                contact.LeadStatus,
                conversation.LastCustomerMessageAt,
                followUp is null ? null : ToInput(followUp),
                BotWillSend: settings.CareAutoSend),
            cancellationToken);

        var now = time.GetUtcNow();
        conversation.CareAnalyzedAt = now;
        if (result is null)
        {
            await conversations.SaveChangesAsync(cancellationToken);
            return;
        }

        foreach (var u in result.Usage)
        {
            conversations.AddUsage(tenantId, new UsageRecord
            {
                Kind = u.Kind,
                Provider = u.Provider,
                Model = u.Model,
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CostUsd = u.CostUsd,
                ConversationId = conversation.Id,
            });
        }

        var decision = CareDecision.Decide(result, contact, conversation, settings, followUp is not null, now);
        CareSuggestion? suggestion = null;
        if (decision.Kind != CareDecisionKind.Nothing)
        {
            suggestion = await UpsertSuggestionAsync(tenantId, conversation, contact, result, cancellationToken);
            suggestion.ScheduledSendAt = decision.Kind == CareDecisionKind.AutoSend ? decision.SendAt : null;
            suggestion.EscalationReason = decision.Kind == CareDecisionKind.Escalate ? decision.Reason : null;
            LeadRules.Promote(contact, LeadRules.FromCare(result.Temperature), now);
        }

        await conversations.SaveChangesAsync(cancellationToken);
        if (suggestion is null)
        {
            return;
        }

        var suggestionId = suggestion.Id;
        var conversationId = conversation.Id;
        if (decision.Kind == CareDecisionKind.AutoSend && decision.SendAt is { } sendAt)
        {
            if (sendAt <= now)
            {
                jobs.Enqueue<ProactiveSendJob>(job => job.RunAsync(tenantId, suggestionId, CancellationToken.None));
            }
            else
            {
                jobs.Schedule<ProactiveSendJob>(job => job.RunAsync(tenantId, suggestionId, CancellationToken.None), sendAt);
            }
        }
        else if (decision.Kind == CareDecisionKind.Escalate)
        {
            jobs.Enqueue<AttentionNotifyJob>(job => job.RunAsync(tenantId, conversationId, AttentionNotifyJob.Care, CancellationToken.None));
        }

        await notifier.PublishAsync(tenantId, new InboxEvent("care", conversation.Id), cancellationToken);
    }

    private async Task<CareSuggestion> UpsertSuggestionAsync(
        Guid tenantId,
        Conversation conversation,
        Contact contact,
        CareAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var suggestion = await contacts.GetOpenSuggestionAsync(tenantId, contact.Id, cancellationToken)
            ?? contacts.AddSuggestion(tenantId, new CareSuggestion
            {
                ContactId = contact.Id,
                ConversationId = conversation.Id,
                Trigger = result.Trigger,
                ReasonEnc = "",
                Status = CareSuggestionStatus.Open,
            });

        suggestion.ConversationId = conversation.Id;
        suggestion.Temperature = result.Temperature;
        suggestion.Trigger = result.Trigger;
        suggestion.ReasonEnc = encryptor.Encrypt(result.Reason);
        suggestion.SuggestedActionEnc = result.SuggestedAction is null ? null : encryptor.Encrypt(result.SuggestedAction);
        suggestion.DraftEnc = result.Draft is null ? null : encryptor.Encrypt(result.Draft);
        suggestion.MessagingDeadline = MessagingDeadline(conversation);
        suggestion.AssignedUserId ??= conversation.AssignedUserId; // nhân viên chat gần nhất với khách
        return suggestion;
    }

    /// <summary>Hạn còn nhắn được qua kênh: Zalo 7 ngày sau tin cuối của khách; chat thử không giới hạn.</summary>
    public static DateTimeOffset? MessagingDeadline(Conversation conversation) => CareDecision.MessagingDeadline(conversation);

    private CareNoteInput ToInput(ContactNote n) =>
        new(n.Kind.ToString().ToLowerInvariant(), n.HappenedOn, n.FollowUpAt, encryptor.Decrypt(n.ContentEnc));
}

/// <summary>
/// Bot gửi tin chăm sóc chủ động đã hẹn giờ. Kiểm tra lại mọi điều kiện ngay lúc gửi (khách có thể vừa trả lời, vừa nhắn "hủy",
/// nhân viên vừa tiếp quản, DN vừa tắt tự nhắn). Chạy lại an toàn: gợi ý đã gửi thì chỉ gửi nốt tin còn pending.
/// </summary>
[Queue(JobQueues.Messages)]
public sealed class ProactiveSendJob(
    TenantContext tenantContext,
    ConversationRepository conversations,
    ContactRepository contacts,
    HandoffSettingsRepository handoffSettings,
    MessageDeliveryService delivery,
    IFieldEncryptor encryptor,
    IInboxNotifier notifier,
    IBackgroundJobClient jobs,
    TimeProvider time)
{
    public async Task RunAsync(Guid tenantId, Guid suggestionId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var suggestion = await contacts.GetSuggestionAsync(tenantId, suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return;
        }

        var conversation = await conversations.GetConversationAsync(tenantId, suggestion.ConversationId, cancellationToken);
        var contact = await contacts.GetAsync(tenantId, suggestion.ContactId, cancellationToken);
        if (conversation is null || contact is null)
        {
            return;
        }

        if (suggestion.Status == CareSuggestionStatus.AutoSent)
        {
            // Lần chạy trước đã lưu tin nhưng chưa gửi xong (mạng lỗi) → gửi nốt, không tạo tin mới.
            if (suggestion.SentMessageId is { } sentId
                && await conversations.GetMessageAsync(tenantId, sentId, cancellationToken) is { DeliveryStatus: DeliveryStatus.Pending } pending)
            {
                await delivery.DeliverAsync(tenantId, conversation, contact, pending, cancellationToken);
            }

            return;
        }

        var now = time.GetUtcNow();
        if (suggestion.Status != CareSuggestionStatus.Open || suggestion.ScheduledSendAt is not { } scheduled || scheduled > now.AddMinutes(1))
        {
            return; // đã xử lý, đã chuyển nhân viên, hoặc đã hẹn lại giờ khác (job khác sẽ gửi)
        }

        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        string? escalate = null;
        if (contact.ProactiveOptOutAt is not null)
        {
            suggestion.Status = CareSuggestionStatus.Skipped;
            suggestion.Outcome = "opted_out";
            suggestion.ResolvedAt = now;
            suggestion.ScheduledSendAt = null;
        }
        else if (!settings.CareAutoSend)
        {
            suggestion.ScheduledSendAt = null; // DN vừa tắt tự nhắn → thành gợi ý cho nhân viên
        }
        else if (conversation.Mode == ConversationMode.Human || conversation.NeedsAttentionSince is not null)
        {
            escalate = "staff_handling";
        }
        else if (suggestion.DraftEnc is null)
        {
            escalate = "draft_blocked";
        }
        else if (CareDecision.MessagingDeadline(conversation) is { } deadline && deadline <= now)
        {
            escalate = "outside_window";
        }
        else if (!CareDecision.InSendWindow(settings, now))
        {
            var next = CareDecision.NextSendTime(settings, now);
            suggestion.ScheduledSendAt = next;
            await contacts.SaveChangesAsync(cancellationToken);
            jobs.Schedule<ProactiveSendJob>(job => job.RunAsync(tenantId, suggestionId, CancellationToken.None), next);
            return;
        }
        else
        {
            var message = delivery.AddOutgoing(tenantId, conversation, MessageSender.Bot, encryptor.Decrypt(suggestion.DraftEnc));
            message.Proactive = true;
            suggestion.Status = CareSuggestionStatus.AutoSent;
            suggestion.SentMessageId = message.Id;
            suggestion.ResolvedAt = now;
            suggestion.ScheduledSendAt = null;
            contact.LastProactiveAt = now;
            contact.ProactiveAwaitingReply = true;
            await conversations.SaveChangesAsync(cancellationToken);
            await delivery.DeliverAsync(tenantId, conversation, contact, message, cancellationToken);
            await notifier.PublishAsync(tenantId, new InboxEvent("message", conversation.Id), cancellationToken);
            return;
        }

        if (escalate is not null)
        {
            suggestion.ScheduledSendAt = null;
            suggestion.EscalationReason = escalate;
        }

        await contacts.SaveChangesAsync(cancellationToken);
        if (escalate is not null)
        {
            var conversationId = conversation.Id;
            jobs.Enqueue<AttentionNotifyJob>(job => job.RunAsync(tenantId, conversationId, AttentionNotifyJob.Care, CancellationToken.None));
        }

        await notifier.PublishAsync(tenantId, new InboxEvent("care", conversation.Id), cancellationToken);
    }
}

/// <summary>
/// Quét 15 phút/lần (job HỆ THỐNG, mọi tenant — IgnoreQueryFilters có chủ đích, chỉ đọc id + mốc thời gian, việc phân tích chạy trong
/// job theo từng tenant): hội thoại "nguội" (khách im quá số giờ cài đặt, còn trong hạn nhắn 7 ngày, chưa phân tích từ tin mới nhất)
/// → phân tích.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class CareSweepJob(AppDbContext db, IBackgroundJobClient jobs, IDistributedStore store, TimeProvider time)
{
    public const string RecurringId = "care-sweep";

    /// <summary>Giới hạn số lần gọi AI mỗi lượt quét (kiểm soát chi phí; phần còn lại để lượt sau).</summary>
    internal const int MaxPerSweep = 100;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var windowStart = now - CareAnalysisJob.ZaloMessagingWindow;
        var cold = await (
            from c in db.Conversations.IgnoreQueryFilters()
            join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
            join s in db.HandoffSettings.IgnoreQueryFilters() on c.TenantId equals s.TenantId into settings
            from s in settings.DefaultIfEmpty()
            where !c.IsTest && c.Status == ConversationStatus.Open && c.NeedsAttentionSince == null
                  && t.Status == TenantStatus.Active && (s == null || s.CareEnabled)
                  && c.LastCustomerMessageAt != null && c.LastCustomerMessageAt > windowStart
                  && (c.CareAnalyzedAt == null || c.CareAnalyzedAt < c.LastCustomerMessageAt)
            select new { c.Id, c.TenantId, c.LastCustomerMessageAt, Hours = s == null ? 6 : s.CareColdHours })
            .AsNoTracking()
            .OrderBy(x => x.LastCustomerMessageAt)
            .Take(MaxPerSweep * 3)
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var c in cold.Where(c => c.LastCustomerMessageAt!.Value.AddHours(c.Hours) <= now))
        {
            if (queued >= MaxPerSweep)
            {
                break;
            }

            // Lượt quét sau chạy khi job trước chưa xong → không đẩy trùng.
            if (await store.SetIfNotExistsAsync($"care:queued:{c.Id:N}:{c.LastCustomerMessageAt!.Value.ToUnixTimeSeconds()}", "1", TimeSpan.FromHours(2), cancellationToken))
            {
                jobs.Enqueue<CareAnalysisJob>(job => job.AnalyzeConversationAsync(c.TenantId, c.Id, CancellationToken.None));
                queued++;
            }
        }
    }
}

/// <summary>
/// Quét MỖI PHÚT (job HỆ THỐNG, mọi tenant — IgnoreQueryFilters có chủ đích, chỉ đọc id + mốc thời gian):
/// 1. Ghi chú tới giờ hẹn chăm sóc lại → phân tích kèm ghi chú đó (đúng giờ, trễ tối đa ~1 phút).
/// 2. Tin chủ động đã hẹn giờ mà quá 5 phút chưa gửi (job hẹn giờ bị mất) → đẩy lại.
/// 3. Gợi ý quá hạn nhắn qua kênh → hết hạn.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class CareFollowUpSweepJob(AppDbContext db, IBackgroundJobClient jobs, IDistributedStore store, TimeProvider time)
{
    public const string RecurringId = "care-follow-up-sweep";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var dueNotes = await (
            from n in db.ContactNotes.IgnoreQueryFilters()
            join t in db.Tenants.IgnoreQueryFilters() on n.TenantId equals t.Id
            where n.FollowUpAt != null && n.FollowUpAt <= now && n.FollowUpQueuedAt == null && t.Status == TenantStatus.Active
            select new { n.Id, n.TenantId })
            .AsNoTracking()
            .Take(CareSweepJob.MaxPerSweep)
            .ToListAsync(cancellationToken);
        foreach (var n in dueNotes)
        {
            if (await store.SetIfNotExistsAsync($"care:followup:{n.Id:N}", "1", TimeSpan.FromHours(2), cancellationToken))
            {
                jobs.Enqueue<CareAnalysisJob>(job => job.FollowUpAsync(n.TenantId, n.Id, CancellationToken.None));
            }
        }

        var missed = now.AddMinutes(-5);
        var lateSends = await (
            from s in db.CareSuggestions.IgnoreQueryFilters()
            join t in db.Tenants.IgnoreQueryFilters() on s.TenantId equals t.Id
            where s.Status == CareSuggestionStatus.Open && s.ScheduledSendAt != null && s.ScheduledSendAt < missed && t.Status == TenantStatus.Active
            select new { s.Id, s.TenantId })
            .AsNoTracking()
            .Take(CareSweepJob.MaxPerSweep)
            .ToListAsync(cancellationToken);
        foreach (var s in lateSends)
        {
            if (await store.SetIfNotExistsAsync($"care:late-send:{s.Id:N}", "1", TimeSpan.FromMinutes(30), cancellationToken))
            {
                jobs.Enqueue<ProactiveSendJob>(job => job.RunAsync(s.TenantId, s.Id, CancellationToken.None));
            }
        }

        // Cập nhật hàng loạt bỏ qua kiểm tra tenant của SaveChanges: an toàn vì chỉ đổi trạng thái theo hạn, không đọc/ghi nội dung.
        await db.CareSuggestions.IgnoreQueryFilters()
            .Where(s => s.Status == CareSuggestionStatus.Open && s.MessagingDeadline != null && s.MessagingDeadline < now)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, CareSuggestionStatus.Expired).SetProperty(s => s.UpdatedAt, now), cancellationToken);
    }
}
