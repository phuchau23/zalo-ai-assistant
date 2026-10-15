using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

/// <param name="Messages">Câu hệ thống nối sau các tin của AI.</param>
/// <param name="HandoffReason">Khác null = chuyển nhân viên (nhóm đông, dịch vụ không rõ, quá hạn hủy...).</param>
public sealed record BookingOutcome(IReadOnlyList<string> Messages, string? HandoffReason)
{
    public static readonly BookingOutcome None = new([], null);
}

/// <summary>
/// Nối AI với hệ thống lịch (CRM C2): AI chỉ rút ý định (<see cref="BookingIntent"/>), lớp này kiểm tra chỗ trống qua
/// <see cref="AppointmentService"/> rồi tự soạn câu xác nhận / gợi ý giờ khác / từ chối theo chính sách. Không bao giờ để AI
/// tự nói "đã đặt". Mọi trường hợp lạ → chuyển nhân viên thay vì đoán.
/// </summary>
public sealed class BookingOrchestrator(
    AppDbContext db,
    ITenantContext tenantContext,
    AppointmentService appointments,
    AvailabilityService availability,
    BranchService branches,
    CrmSettingsRepository settingsRepository,
    IBackgroundJobClient jobs,
    TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    public const string HandoffBooking = "booking";

    /// <summary>Bối cảnh đặt lịch đưa vào prompt (bật/tắt, chi nhánh, lịch sắp tới của khách).</summary>
    public async Task<BookingContext> ContextAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        if (!settings.BotBookingEnabled)
        {
            return new BookingContext(false, [], []);
        }

        var zone = CrmClock.Zone(settings.TimeZone);
        var branchNames = (await branches.ListAsync(tenantId, includeInactive: false, cancellationToken)).Select(b => b.Name).ToList();
        var upcoming = (await appointments.UpcomingForContactAsync(tenantId, contactId, 3, cancellationToken))
            .Select(v => new UpcomingAppointment(v.Appointment.Id, CrmClock.FormatFull(v.Appointment.StartAt, zone), AppointmentService.DescribeItems(v.Items), v.StaffName, v.BranchName))
            .ToList();
        return new BookingContext(true, branchNames, upcoming);
    }

    public async Task<BookingOutcome> HandleAsync(Guid tenantId, Tenant tenant, Conversation conversation, Contact contact, BookingIntent intent, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(intent);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        if (!settings.BotBookingEnabled)
        {
            return BookingOutcome.None;
        }

        var bot = tenant.BotPronoun;
        var zone = CrmClock.Zone(settings.TimeZone);
        if (intent.PartySize > settings.BotMaxPartySize)
        {
            return new BookingOutcome([], HandoffBooking);
        }

        return intent.Action switch
        {
            "book" => await BookAsync(tenantId, tenant, conversation, contact, intent, settings, zone, cancellationToken),
            "check_slots" => await CheckSlotsAsync(tenantId, tenant, contact, intent, settings, zone, cancellationToken),
            "reschedule" => await RescheduleAsync(tenantId, tenant, contact, intent, settings, zone, cancellationToken),
            "cancel" => await CancelAsync(tenantId, tenant, contact, intent, zone, cancellationToken),
            _ => BookingOutcome.None,
        };
    }

    /// <summary>Khách trả lời "ok/đồng ý/xác nhận" sau tin nhắc lịch → xác nhận lịch, không cần AI. true = đã xử lý.</summary>
    public async Task<string?> TryConfirmByReplyAsync(Guid tenantId, Tenant tenant, Contact contact, string text, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(contact);
        if (!IsConfirmReply(text))
        {
            return null;
        }

        var now = time.GetUtcNow();
        var pending = await Db.Appointments
            .Where(a => a.TenantId == tenantId && a.ContactId == contact.Id && a.Status == AppointmentStatus.Booked && a.StartAt > now
                        && (a.Reminder1SentAt != null || a.Reminder2SentAt != null))
            .OrderBy(a => a.StartAt).FirstOrDefaultAsync(cancellationToken);
        if (pending is null)
        {
            return null;
        }

        await appointments.SetStatusAsync(tenantId, pending.Id, AppointmentStatus.Confirmed, Actor.Customer, cancellationToken);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var khach = CustomerSalutation.Resolve(contact.Salutation);
        return $"Dạ {tenant.BotPronoun} đã ghi nhận {khach} xác nhận lịch {CrmClock.FormatForCustomer(pending.StartAt, CrmClock.Zone(settings.TimeZone))} ạ. Hẹn gặp {khach} 🌿";
    }

    private async Task<BookingOutcome> BookAsync(Guid tenantId, Tenant tenant, Conversation conversation, Contact contact, BookingIntent intent, CrmSettings settings, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var bot = tenant.BotPronoun;
        if (intent.ServiceCodes.Count == 0)
        {
            return new BookingOutcome([$"{Cap(CustomerSalutation.Resolve(contact.Salutation))} muốn làm dịch vụ nào để {bot} giữ lịch ạ?"], null);
        }

        var items = await ResolveServicesAsync(tenantId, intent.ServiceCodes, cancellationToken);
        if (items is null || items.Any(i => i.Price is null))
        {
            return new BookingOutcome([], HandoffBooking); // dịch vụ không rõ / liên hệ báo giá → nhân viên
        }

        if (!TryParseLocal(intent.Date, intent.Time, zone, out var start))
        {
            return new BookingOutcome([AppointmentTexts.NeedTime(bot, contact.Salutation)], null);
        }

        var branch = await ResolveBranchAsync(tenantId, intent.BranchName, cancellationToken);
        var multiBranch = await Db.Branches.CountAsync(b => b.TenantId == tenantId && b.Status == BranchStatus.Active, cancellationToken) > 1;
        var staffId = await ResolveStaffAsync(tenantId, branch.Id, intent.StaffName, cancellationToken);
        var requirement = await availability.RequirementAsync(tenantId, items.Select(i => i.Id).ToList(), settings, cancellationToken);

        try
        {
            var appointment = await appointments.CreateAsync(
                tenantId,
                new AppointmentInput(branch.Id, contact.Id, null, staffId, null, start, items.Select(i => i.Id).ToList(), null, intent.PartySize, AppointmentSource.Bot, conversation.Id),
                Actor.Bot,
                cancellationToken);
            var staffName = appointment.StaffMemberId is { } s
                ? await Db.StaffMembers.AsNoTracking().Where(m => m.Id == s).Select(m => m.DisplayName).FirstOrDefaultAsync(cancellationToken)
                : null;
            var appointmentId = appointment.Id;
            jobs.Enqueue<AppointmentBriefJob>(job => job.RunAsync(tenantId, appointmentId, CancellationToken.None));
            return new BookingOutcome(
            [
                AppointmentTexts.Confirmed(bot, contact.Salutation, appointment.StartAt, zone, string.Join(" + ", items.Select(i => i.Name)), staffName, branch.Name, branch.Address, multiBranch),
                AppointmentTexts.ConfirmedFollowUp(bot, contact.Salutation, settings.CancelLeadHours),
            ], null);
        }
        catch (ConflictException ex) when (ex.Code is "slot_unavailable" or "too_soon")
        {
            var options = await availability.SuggestAsync(tenantId, branch.Id, CrmClock.LocalDate(start, zone), CrmClock.LocalTime(start, zone), requirement, staffId, 3, cancellationToken);
            return new BookingOutcome([AppointmentTexts.Alternatives(bot, contact.Salutation, start, zone, options.Select(o => o.StartAt).ToList())], null);
        }
        catch (ConflictException ex) when (ex.Code is "too_far")
        {
            return new BookingOutcome([$"Dạ bên {bot} chỉ nhận lịch trước tối đa {settings.MaxBookingDaysAhead} ngày ạ. {Cap(CustomerSalutation.Resolve(contact.Salutation))} chọn ngày gần hơn giúp {bot} nha."], null);
        }
        catch (AppException)
        {
            return new BookingOutcome([], HandoffBooking);
        }
    }

    private async Task<BookingOutcome> CheckSlotsAsync(Guid tenantId, Tenant tenant, Contact contact, BookingIntent intent, CrmSettings settings, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var bot = tenant.BotPronoun;
        var items = intent.ServiceCodes.Count == 0 ? null : await ResolveServicesAsync(tenantId, intent.ServiceCodes, cancellationToken);
        if (items is null)
        {
            return new BookingOutcome([$"{Cap(CustomerSalutation.Resolve(contact.Salutation))} muốn xem giờ trống cho dịch vụ nào ạ?"], null);
        }

        var date = TryParseLocal(intent.Date, "09:00", zone, out var start) ? CrmClock.LocalDate(start, zone) : CrmClock.LocalDate(time.GetUtcNow(), zone);
        var branch = await ResolveBranchAsync(tenantId, intent.BranchName, cancellationToken);
        var staffId = await ResolveStaffAsync(tenantId, branch.Id, intent.StaffName, cancellationToken);
        try
        {
            var requirement = await availability.RequirementAsync(tenantId, items.Select(i => i.Id).ToList(), settings, cancellationToken);
            var slots = await availability.SlotsAsync(tenantId, branch.Id, date, requirement, staffId, null, cancellationToken);
            var distinct = slots.Select(s => s.StartAt).Distinct().OrderBy(s => s).ToList();
            var shown = distinct.Count <= 6 ? distinct : distinct.Where((_, i) => i % Math.Ceiling(distinct.Count / 6.0) == 0).Take(6).ToList();
            return new BookingOutcome([AppointmentTexts.SlotList(bot, contact.Salutation, date, zone, shown)], null);
        }
        catch (AppException)
        {
            return new BookingOutcome([], HandoffBooking);
        }
    }

    private async Task<BookingOutcome> RescheduleAsync(Guid tenantId, Tenant tenant, Contact contact, BookingIntent intent, CrmSettings settings, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var bot = tenant.BotPronoun;
        var appointment = await OwnUpcomingAsync(tenantId, contact.Id, intent.AppointmentId, cancellationToken);
        if (appointment is null)
        {
            return new BookingOutcome([AppointmentTexts.NotFound(bot, contact.Salutation)], HandoffBooking);
        }

        if (!TryParseLocal(intent.Date ?? CrmClock.LocalDate(appointment.StartAt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), intent.Time, zone, out var newStart))
        {
            return new BookingOutcome([AppointmentTexts.NeedTime(bot, contact.Salutation)], null);
        }

        try
        {
            var old = appointment.StartAt;
            var updated = await appointments.RescheduleAsync(tenantId, appointment.Id, newStart, null, Actor.Customer, cancellationToken);
            return new BookingOutcome([AppointmentTexts.Rescheduled(bot, contact.Salutation, old, updated.StartAt, zone)], null);
        }
        catch (ConflictException ex) when (ex.Code == "cancel_too_late")
        {
            return new BookingOutcome([AppointmentTexts.TooLate(bot, contact.Salutation, settings.CancelLeadHours, tenant.Name)], HandoffBooking);
        }
        catch (ConflictException ex) when (ex.Code is "slot_unavailable" or "too_soon")
        {
            var items = await Db.AppointmentItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppointmentId == appointment.Id).Select(i => i.CatalogItemId).ToListAsync(cancellationToken);
            var requirement = await availability.RequirementAsync(tenantId, items, settings, cancellationToken);
            var options = await availability.SuggestAsync(tenantId, appointment.BranchId, CrmClock.LocalDate(newStart, zone), CrmClock.LocalTime(newStart, zone), requirement, null, 3, cancellationToken);
            return new BookingOutcome([AppointmentTexts.Alternatives(bot, contact.Salutation, newStart, zone, options.Select(o => o.StartAt).ToList())], null);
        }
        catch (AppException)
        {
            return new BookingOutcome([], HandoffBooking);
        }
    }

    private async Task<BookingOutcome> CancelAsync(Guid tenantId, Tenant tenant, Contact contact, BookingIntent intent, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var bot = tenant.BotPronoun;
        var appointment = await OwnUpcomingAsync(tenantId, contact.Id, intent.AppointmentId, cancellationToken);
        if (appointment is null)
        {
            return new BookingOutcome([AppointmentTexts.NotFound(bot, contact.Salutation)], HandoffBooking);
        }

        try
        {
            await appointments.CancelAsync(tenantId, appointment.Id, "Khách hủy qua chat", Actor.Customer, cancellationToken);
            var (branchId, startAt, endAt) = (appointment.BranchId, appointment.StartAt, appointment.EndAt);
            jobs.Enqueue<WaitlistMatchJob>(job => job.RunAsync(tenantId, branchId, startAt, endAt, CancellationToken.None));
            return new BookingOutcome([AppointmentTexts.Cancelled(bot, contact.Salutation, appointment.StartAt, zone)], null);
        }
        catch (ConflictException ex) when (ex.Code == "cancel_too_late")
        {
            var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
            return new BookingOutcome([AppointmentTexts.TooLate(bot, contact.Salutation, settings.CancelLeadHours, tenant.Name)], HandoffBooking);
        }
        catch (AppException)
        {
            return new BookingOutcome([], HandoffBooking);
        }
    }

    private async Task<Appointment?> OwnUpcomingAsync(Guid tenantId, Guid contactId, Guid? appointmentId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var query = Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ContactId == contactId && a.StartAt > now && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed));
        if (appointmentId is { } id)
        {
            return await query.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        }

        // Khách chỉ có một lịch sắp tới → hiểu là lịch đó.
        var list = await query.OrderBy(a => a.StartAt).Take(2).ToListAsync(cancellationToken);
        return list.Count == 1 ? list[0] : null;
    }

    private async Task<List<CatalogItem>?> ResolveServicesAsync(Guid tenantId, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var items = await Db.CatalogItems.AsNoTracking()
            .Where(c => c.TenantId == tenantId && codes.Contains(c.Code) && c.IsActive && c.Kind != CatalogKind.Product)
            .ToListAsync(cancellationToken);
        return items.Count == codes.Count ? codes.Select(c => items.First(i => i.Code == c)).ToList() : null;
    }

    private async Task<Branch> ResolveBranchAsync(Guid tenantId, string? branchName, CancellationToken cancellationToken)
    {
        var active = await Db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId && b.Status == BranchStatus.Active).ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            var match = active.FirstOrDefault(b => b.Name.Contains(branchName, StringComparison.OrdinalIgnoreCase) || branchName.Contains(b.Name, StringComparison.OrdinalIgnoreCase) || b.Code.Equals(branchName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return active.FirstOrDefault(b => b.IsDefault) ?? active.FirstOrDefault() ?? await branches.EnsureDefaultAsync(tenantId, cancellationToken);
    }

    private async Task<Guid?> ResolveStaffAsync(Guid tenantId, Guid branchId, string? staffName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(staffName))
        {
            return null;
        }

        var staff = await Db.StaffMembers.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.BranchId == branchId && s.Status == StaffStatus.Active && s.Bookable)
            .Select(s => new { s.Id, s.DisplayName }).ToListAsync(cancellationToken);
        return staff.FirstOrDefault(s => s.DisplayName.Contains(staffName, StringComparison.OrdinalIgnoreCase) || staffName.Contains(s.DisplayName, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public static bool TryParseLocal(string? date, string? timeText, TimeZoneInfo zone, out DateTimeOffset start)
    {
        start = default;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            || !TimeOnly.TryParseExact(timeText, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return false;
        }

        start = CrmClock.FromLocal(d, t, zone);
        return true;
    }

    public static bool IsConfirmReply(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().TrimEnd('.', '!', ' ');
        return t is "ok" or "oke" or "okay" or "okie" or "đồng ý" or "xác nhận" or "ok nha" or "ok nhé" or "ok ạ" or "dạ ok" or "ừ" or "được" or "dạ được" or "vâng";
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
