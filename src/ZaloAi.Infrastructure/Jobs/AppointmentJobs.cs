using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Quét mỗi phút (job HỆ THỐNG, IgnoreQueryFilters có chủ đích — chỉ lấy tenant_id của các lịch trong 48 giờ tới) → job nhắc lịch
/// của từng tenant. Không đọc dữ liệu nghiệp vụ ở đây.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class AppointmentReminderSweepJob(AppDbContext db, IBackgroundJobClient jobs, TimeProvider time)
{
    public const string RecurringId = "appointment-reminder-sweep";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var horizon = now.AddHours(48);
        var tenantIds = await db.Appointments.IgnoreQueryFilters()
            .Where(a => a.StartAt > now && a.StartAt <= horizon
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed)
                        && (a.Reminder1SentAt == null || a.Reminder2SentAt == null))
            .Select(a => a.TenantId).Distinct().ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
        {
            jobs.Enqueue<AppointmentReminderJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        }
    }
}

/// <summary>
/// Nhắc lịch của MỘT tenant (CRM C2): trước N giờ (cài đặt), gửi qua hội thoại đã chốt lịch (hoặc hội thoại gần nhất của khách) nếu
/// còn trong hạn nhắn Zalo và trong khung giờ gửi; khách chỉ có SĐT → ghi sự kiện để nhân viên gọi. Mỗi mốc chỉ gửi một lần.
/// </summary>
[Queue(JobQueues.Messages)]
public sealed partial class AppointmentReminderJob(
    TenantContext tenantContext,
    AppDbContext db,
    AppointmentService appointments,
    ConversationRepository conversations,
    ContactRepository contacts,
    TenantRepository tenants,
    CrmSettingsRepository settingsRepository,
    HandoffSettingsRepository handoffSettings,
    MessageDeliveryService delivery,
    IInboxNotifier notifier,
    TimeProvider time,
    ILogger<AppointmentReminderJob> logger)
{
    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (tenant is null || (settings.Reminder1Hours <= 0 && settings.Reminder2Hours <= 0))
        {
            return;
        }

        var now = time.GetUtcNow();
        var zone = CrmClock.Zone(settings.TimeZone);
        var handoff = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var due = await db.Appointments
            .Where(a => a.TenantId == tenantId && a.StartAt > now && a.StartAt <= now.AddHours(Math.Max(settings.Reminder1Hours, settings.Reminder2Hours))
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.StartAt).Take(200).ToListAsync(cancellationToken);

        foreach (var appointment in due)
        {
            var untilStart = appointment.StartAt - now;
            var first = settings.Reminder1Hours > 0 && appointment.Reminder1SentAt is null && untilStart <= TimeSpan.FromHours(settings.Reminder1Hours);
            var second = settings.Reminder2Hours > 0 && appointment.Reminder2SentAt is null && untilStart <= TimeSpan.FromHours(settings.Reminder2Hours);
            if (!first && !second)
            {
                continue;
            }

            // Nhắc sớm (24h) ngoài khung giờ gửi → đợi tới khung giờ, trừ khi sắp tới giờ hẹn.
            if (first && !second && !CareDecision.InSendWindow(handoff, now) && untilStart > TimeSpan.FromHours(3))
            {
                continue;
            }

            var sent = await SendAsync(tenantId, tenant, settings, zone, appointment, first ? "reminder1" : "reminder2", cancellationToken);
            var stamp = sent ? now : now; // dù không gửi được cũng đánh dấu để không lặp mỗi phút
            if (first)
            {
                appointment.Reminder1SentAt = stamp;
            }

            if (second)
            {
                appointment.Reminder2SentAt = stamp;
            }

            appointments.AddEvent(tenantId, appointment.Id, sent ? "reminder_sent" : "reminder_skipped", Actor.System, new { kind = first ? "reminder1" : "reminder2" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> SendAsync(Guid tenantId, Tenant tenant, CrmSettings settings, TimeZoneInfo zone, Appointment appointment, string kind, CancellationToken cancellationToken)
    {
        var contact = await contacts.GetAsync(tenantId, appointment.ContactId, cancellationToken);
        if (contact is null || contact.ProactiveOptOutAt is not null)
        {
            return false;
        }

        var conversation = appointment.ConversationId is { } cid
            ? await conversations.GetConversationAsync(tenantId, cid, cancellationToken)
            : await contacts.LatestConversationAsync(tenantId, contact.Id, cancellationToken);
        if (conversation is null || conversation.Status == ConversationStatus.Closed)
        {
            return false; // khách chỉ có SĐT → nhân viên gọi (sự kiện reminder_skipped hiện trên lịch)
        }

        if (CareDecision.MessagingDeadline(conversation) is { } deadline && deadline <= time.GetUtcNow())
        {
            LogOutsideWindow(logger, appointment.Id);
            return false;
        }

        var view = (await appointments.ViewsAsync(tenantId, [appointment], includeBrief: false, cancellationToken))[0];
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == appointment.BranchId, cancellationToken);
        var tomorrow = kind == "reminder1" && CrmClock.LocalDate(appointment.StartAt, zone) == CrmClock.LocalDate(time.GetUtcNow(), zone).AddDays(1);
        var text = AppointmentTexts.Reminder(tenant.BotPronoun, contact.Salutation, appointment.StartAt, zone, AppointmentService.DescribeItems(view.Items), branch.Name, branch.Address, tomorrow);
        var message = delivery.AddOutgoing(tenantId, conversation, MessageSender.Bot, text);
        message.Proactive = true;
        await conversations.SaveChangesAsync(cancellationToken);
        await delivery.DeliverAsync(tenantId, conversation, contact, message, cancellationToken);
        await notifier.PublishAsync(tenantId, new InboxEvent("message", conversation.Id), cancellationToken);
        return message.DeliveryStatus == DeliveryStatus.Sent;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lịch {AppointmentId}: quá hạn nhắn qua kênh, bỏ qua nhắc tự động")]
    private static partial void LogOutsideWindow(ILogger logger, Guid appointmentId);
}

/// <summary>Viết tóm tắt khách cho kỹ thuật viên (CRM C2) ngay sau khi chốt lịch; lỗi AI → thử lại theo Hangfire. Kết quả mã hóa.</summary>
[Queue(JobQueues.Default)]
public sealed class AppointmentBriefJob(
    TenantContext tenantContext,
    AppDbContext db,
    AppointmentService appointments,
    ConversationRepository conversations,
    ContactRepository contacts,
    TenantRepository tenants,
    IAppointmentBriefWriter writer,
    IFieldEncryptor encryptor,
    TimeProvider time)
{
    public async Task RunAsync(Guid tenantId, Guid appointmentId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == appointmentId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (appointment is null || tenant is null || !appointment.IsActive)
        {
            return;
        }

        var contact = await contacts.GetAsync(tenantId, appointment.ContactId, cancellationToken) ?? throw new NotFoundException();
        var view = (await appointments.ViewsAsync(tenantId, [appointment], includeBrief: false, cancellationToken))[0];
        var notes = (await contacts.ListNotesAsync(tenantId, contact.Id, 10, cancellationToken)).Select(n => encryptor.Decrypt(n.ContentEnc)).ToList();
        var previous = await db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ContactId == contact.Id && a.Status == AppointmentStatus.Done && a.Id != appointmentId)
            .OrderByDescending(a => a.StartAt).Take(3).ToListAsync(cancellationToken);
        var previousViews = await appointments.ViewsAsync(tenantId, previous, includeBrief: false, cancellationToken);
        var zone = CrmClock.Zone((await db.CrmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken))?.TimeZone);

        string? summary = null;
        var recent = new List<BotHistoryTurn>();
        if (appointment.ConversationId is { } cid && await conversations.GetConversationAsync(tenantId, cid, cancellationToken) is { } conversation)
        {
            summary = conversation.SummaryEnc is null ? null : encryptor.Decrypt(conversation.SummaryEnc);
            recent = (await conversations.ListMessagesAsync(tenantId, cid, conversation.SummarizedUntil, cancellationToken))
                .TakeLast(12).Select(m => new BotHistoryTurn(m.Sender, encryptor.Decrypt(m.ContentEnc))).ToList();
        }

        var result = await writer.WriteAsync(tenantId, new AppointmentBriefRequest(
            tenant.Name,
            tenant.IndustrySlug,
            view.ContactName,
            view.Items.Select(i => i.Name).ToList(),
            notes,
            summary,
            recent,
            previousViews.Select(p => $"{CrmClock.ToLocal(p.Appointment.StartAt, zone):dd/MM}: {AppointmentService.DescribeItems(p.Items)}").ToList()), cancellationToken);

        appointment.AiBriefEnc = encryptor.Encrypt(result.Brief);
        appointment.AiBriefAt = time.GetUtcNow();
        foreach (var u in result.Usage)
        {
            conversations.AddUsage(tenantId, new UsageRecord { Kind = u.Kind, Provider = u.Provider, Model = u.Model, InputTokens = u.InputTokens, OutputTokens = u.OutputTokens, CostUsd = u.CostUsd, ConversationId = appointment.ConversationId });
        }

        appointments.AddEvent(tenantId, appointment.Id, "brief_written", Actor.System, null);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Có chỗ trống (ai đó hủy) → nhắn khách đang chờ phù hợp (có hội thoại, trong hạn nhắn, chưa từ chối tin chủ động). Mỗi khách một lần.</summary>
[Queue(JobQueues.Messages)]
public sealed class WaitlistMatchJob(
    TenantContext tenantContext,
    AppDbContext db,
    AvailabilityService availability,
    CrmSettingsRepository settingsRepository,
    ConversationRepository conversations,
    ContactRepository contacts,
    TenantRepository tenants,
    MessageDeliveryService delivery,
    TimeProvider time)
{
    public async Task RunAsync(Guid tenantId, Guid branchId, DateTimeOffset freedStart, DateTimeOffset freedEnd, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var zone = CrmClock.Zone(settings.TimeZone);
        var waiting = await db.WaitlistEntries
            .Where(w => w.TenantId == tenantId && w.BranchId == branchId && w.Status == WaitlistStatus.Waiting && w.PreferredFrom < freedEnd && w.PreferredTo > freedStart)
            .OrderBy(w => w.CreatedAt).Take(10).ToListAsync(cancellationToken);
        foreach (var entry in waiting)
        {
            var contact = await contacts.GetAsync(tenantId, entry.ContactId, cancellationToken);
            if (contact is null || contact.ProactiveOptOutAt is not null)
            {
                continue;
            }

            var conversation = await contacts.LatestConversationAsync(tenantId, contact.Id, cancellationToken);
            if (conversation is null || (CareDecision.MessagingDeadline(conversation) is { } deadline && deadline <= time.GetUtcNow()))
            {
                continue;
            }

            ServiceRequirement requirement;
            try
            {
                requirement = await availability.RequirementAsync(tenantId, [entry.CatalogItemId], settings, cancellationToken);
            }
            catch (AppException)
            {
                continue;
            }

            var slot = await availability.CheckAsync(tenantId, branchId, freedStart, requirement, entry.StaffMemberId, null, cancellationToken);
            if (slot is null)
            {
                return; // chỗ đã được lấy
            }

            var service = await db.CatalogItems.AsNoTracking().Where(c => c.Id == entry.CatalogItemId).Select(c => c.Name).FirstAsync(cancellationToken);
            var message = delivery.AddOutgoing(tenantId, conversation, MessageSender.Bot, AppointmentTexts.WaitlistOffer(tenant.BotPronoun, contact.Salutation, freedStart, zone, service));
            message.Proactive = true;
            entry.Status = WaitlistStatus.Offered;
            entry.OfferedAt = time.GetUtcNow();
            entry.OfferedSlotAt = freedStart;
            await conversations.SaveChangesAsync(cancellationToken);
            await delivery.DeliverAsync(tenantId, conversation, contact, message, cancellationToken);
            return; // một khách một lần; khách không nhận thì nhân viên xử lý tiếp trên hàng chờ
        }
    }
}
