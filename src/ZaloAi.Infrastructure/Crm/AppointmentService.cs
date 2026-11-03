using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

/// <summary>Khách mới đặt tay (chưa có hồ sơ): tạo hồ sơ chỉ có SĐT (hoặc không có kênh).</summary>
public sealed record NewContactInput(string Name, string? Phone, string? Salutation);

/// <param name="AllowOverlap">Nhân viên cố tình xếp chồng (khách VIP) — bỏ kiểm tra chỗ trống; bot KHÔNG được dùng.</param>
public sealed record AppointmentInput(
    Guid? BranchId,
    Guid? ContactId,
    NewContactInput? NewContact,
    Guid? StaffMemberId,
    Guid? ResourceId,
    DateTimeOffset StartAt,
    IReadOnlyList<Guid> CatalogItemIds,
    string? Note,
    int PartySize,
    AppointmentSource Source,
    Guid? ConversationId = null,
    bool AllowOverlap = false);

/// <summary>Ai thao tác: nhân viên (userId), bot, khách (qua chat), hệ thống (job).</summary>
public sealed record Actor(string Kind, Guid? UserId)
{
    public static readonly Actor Bot = new("bot", null);
    public static readonly Actor Customer = new("customer", null);
    public static readonly Actor System = new("system", null);

    public static Actor Staff(Guid? userId) => new("staff", userId);
}

public sealed record AppointmentView(
    Appointment Appointment,
    IReadOnlyList<AppointmentItem> Items,
    string ContactName,
    string? ContactPhone,
    string? ContactSalutation,
    string? StaffName,
    string BranchName,
    string? Note,
    string? AiBrief);

public sealed record AppointmentDetail(AppointmentView View, IReadOnlyList<AppointmentEvent> Events, string? ContactLeadStatus, int ContactVisits, int ContactNoShows);

/// <summary>Kết quả tạo một buổi trong lịch định kỳ.</summary>
public sealed record SeriesOutcome(DateTimeOffset StartAt, Appointment? Appointment, string? Error);

/// <summary>
/// Lịch hẹn (CRM C2): đặt tay / bot / online, dời, hủy, trạng thái, định kỳ. Mọi lần đặt đều qua <see cref="AvailabilityService"/>
/// trong một transaction có khóa theo (chi nhánh, ngày) → hai yêu cầu cùng lúc không nhận trùng. Ghi chú/tóm tắt AI mã hóa.
/// Nhân viên bị giới hạn chi nhánh chỉ thấy lịch chi nhánh đó.
/// </summary>
public sealed class AppointmentService(
    AppDbContext db,
    ITenantContext tenantContext,
    AvailabilityService availability,
    BranchService branches,
    CrmSettingsRepository settingsRepository,
    IFieldEncryptor encryptor,
    IPhoneHasher phoneHasher,
    TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<AppointmentView>> ListAsync(
        Guid tenantId, Guid? branchId, DateTimeOffset from, DateTimeOffset to, Guid? staffMemberId, Guid? contactId, bool includeCancelled, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        var query = Db.Appointments.AsNoTracking().Where(a => a.TenantId == tenantId && a.StartAt < to && a.EndAt > from);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(a => a.BranchId == b);
        }

        if (staffMemberId is { } s)
        {
            query = query.Where(a => a.StaffMemberId == s);
        }

        if (contactId is { } c)
        {
            query = query.Where(a => a.ContactId == c);
        }

        if (!includeCancelled)
        {
            query = query.Where(a => a.Status != AppointmentStatus.Cancelled);
        }

        var list = await query.OrderBy(a => a.StartAt).Take(2000).ToListAsync(cancellationToken);
        return await ViewsAsync(tenantId, list, includeBrief: false, cancellationToken);
    }

    public async Task<AppointmentDetail> GetAsync(Guid tenantId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var appointment = await RequireAsync(tenantId, appointmentId, tracked: false, cancellationToken);
        var view = (await ViewsAsync(tenantId, [appointment], includeBrief: true, cancellationToken))[0];
        var events = await Db.AppointmentEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.AppointmentId == appointmentId)
            .OrderBy(e => e.At).ToListAsync(cancellationToken);
        var contact = await Db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == appointment.ContactId, cancellationToken);
        var visits = await Db.Appointments.CountAsync(a => a.TenantId == tenantId && a.ContactId == appointment.ContactId && a.Status == AppointmentStatus.Done, cancellationToken);
        var noShows = await Db.Appointments.CountAsync(a => a.TenantId == tenantId && a.ContactId == appointment.ContactId && a.Status == AppointmentStatus.NoShow, cancellationToken);
        return new AppointmentDetail(view, events, contact?.LeadStatus.ToString().ToLowerInvariant(), visits, noShows);
    }

    /// <summary>Lịch sắp tới của một khách (cho prompt bot, hồ sơ khách).</summary>
    public async Task<IReadOnlyList<AppointmentView>> UpcomingForContactAsync(Guid tenantId, Guid contactId, int max, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var now = time.GetUtcNow();
        var list = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ContactId == contactId && a.EndAt > now
                        && (a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.StartAt).Take(max).ToListAsync(cancellationToken);
        return await ViewsAsync(tenantId, list, includeBrief: false, cancellationToken);
    }

    public async Task<Appointment> CreateAsync(Guid tenantId, AppointmentInput input, Actor actor, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actor);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var zone = CrmClock.Zone(settings.TimeZone);
        var branch = input.BranchId is { } bid
            ? await branches.RequireAsync(tenantId, bid, cancellationToken)
            : await branches.EnsureDefaultAsync(tenantId, cancellationToken);
        if (branch.Status != BranchStatus.Active)
        {
            throw new ConflictException("Chi nhánh đang ngừng hoạt động.", "branch_inactive");
        }

        var requirement = await availability.RequirementAsync(tenantId, input.CatalogItemIds, settings, cancellationToken);
        input = input with { StartAt = ToMinute(input.StartAt) };
        ValidateStart(input.StartAt, settings, zone, allowPast: input.AllowOverlap);
        var contact = await ResolveContactAsync(tenantId, input, cancellationToken);
        if (input.ResourceId is { } rid && !await Db.Resources.AnyAsync(r => r.TenantId == tenantId && r.BranchId == branch.Id && r.Id == rid && r.IsActive, cancellationToken))
        {
            throw new NotFoundException();
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        await LockDayAsync(tenantId, branch.Id, CrmClock.LocalDate(input.StartAt, zone), cancellationToken);

        Guid? staffId = input.StaffMemberId;
        if (!input.AllowOverlap)
        {
            var slot = await availability.CheckAsync(tenantId, branch.Id, input.StartAt, requirement, input.StaffMemberId, null, cancellationToken)
                ?? throw new ConflictException("Khung giờ này đã kín hoặc ngoài giờ làm việc.", "slot_unavailable");
            staffId ??= slot.StaffMemberId;
        }
        else if (staffId is { } s && !await Db.StaffMembers.AnyAsync(m => m.TenantId == tenantId && m.Id == s, cancellationToken))
        {
            throw new NotFoundException();
        }

        var appointment = new Appointment
        {
            TenantId = tenantId,
            BranchId = branch.Id,
            ContactId = contact.Id,
            StaffMemberId = staffId,
            ResourceId = input.ResourceId,
            StartAt = input.StartAt.ToUniversalTime(),
            EndAt = input.StartAt.ToUniversalTime().AddMinutes(requirement.DurationMinutes),
            BufferMinutes = requirement.BufferMinutes,
            Source = input.Source,
            BookedByUserId = actor.UserId,
            ConversationId = input.ConversationId,
            PartySize = Math.Max(1, input.PartySize),
            NoteEnc = string.IsNullOrWhiteSpace(input.Note) ? null : encryptor.Encrypt(input.Note.Trim()),
        };
        Db.Appointments.Add(appointment);
        AddItems(tenantId, appointment, requirement.Items);
        AddEvent(tenantId, appointment.Id, "created", actor, new { source = input.Source.ToString().ToLowerInvariant(), start = appointment.StartAt, staff = staffId });
        if (contact.LeadStatus is LeadStatus.New or LeadStatus.Interested or LeadStatus.Hot && !contact.LeadStatusManual)
        {
            contact.LeadStatus = LeadStatus.Won;
            contact.LeadStatusChangedAt = time.GetUtcNow();
        }

        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return appointment;
    }

    public async Task<Appointment> RescheduleAsync(Guid tenantId, Guid appointmentId, DateTimeOffset newStart, Guid? staffMemberId, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var appointment = await RequireAsync(tenantId, appointmentId, tracked: true, cancellationToken);
        if (appointment.Status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed))
        {
            throw new ConflictException("Chỉ dời được lịch chưa diễn ra.", "appointment_state");
        }

        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var zone = CrmClock.Zone(settings.TimeZone);
        EnsureCustomerLead(appointment, actor, settings);
        newStart = ToMinute(newStart);
        ValidateStart(newStart, settings, zone, allowPast: false);
        var items = await Db.AppointmentItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppointmentId == appointmentId).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var requirement = await availability.RequirementAsync(tenantId, items.Select(i => i.CatalogItemId).ToList(), settings, cancellationToken);

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        await LockDayAsync(tenantId, appointment.BranchId, CrmClock.LocalDate(newStart, zone), cancellationToken);
        var slot = await availability.CheckAsync(tenantId, appointment.BranchId, newStart, requirement, staffMemberId ?? appointment.StaffMemberId, appointmentId, cancellationToken)
            ?? (staffMemberId is null && appointment.StaffMemberId is not null
                ? await availability.CheckAsync(tenantId, appointment.BranchId, newStart, requirement, null, appointmentId, cancellationToken)
                : null)
            ?? throw new ConflictException("Khung giờ mới đã kín hoặc ngoài giờ làm việc.", "slot_unavailable");

        var old = appointment.StartAt;
        appointment.StartAt = newStart.ToUniversalTime();
        appointment.EndAt = appointment.StartAt.AddMinutes(requirement.DurationMinutes);
        appointment.StaffMemberId = staffMemberId ?? slot.StaffMemberId ?? appointment.StaffMemberId;
        appointment.Status = AppointmentStatus.Booked;
        appointment.CustomerConfirmedAt = null;
        appointment.Reminder1SentAt = null;
        appointment.Reminder2SentAt = null;
        AddEvent(tenantId, appointment.Id, "rescheduled", actor, new { from = old, to = appointment.StartAt, staff = appointment.StaffMemberId });
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return appointment;
    }

    public async Task<Appointment> CancelAsync(Guid tenantId, Guid appointmentId, string? reason, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var appointment = await RequireAsync(tenantId, appointmentId, tracked: true, cancellationToken);
        if (appointment.Status is AppointmentStatus.Done or AppointmentStatus.Cancelled)
        {
            throw new ConflictException("Lịch đã xong hoặc đã hủy.", "appointment_state");
        }

        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        EnsureCustomerLead(appointment, actor, settings);
        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        appointment.CancelledBy = actor.Kind;
        appointment.CancelledAt = time.GetUtcNow();
        AddEvent(tenantId, appointment.Id, "cancelled", actor, new { reason = appointment.CancelReason });
        await Db.SaveChangesAsync(cancellationToken);
        return appointment;
    }

    /// <summary>Check-in / đang làm / xong / không đến. Chuyển trạng thái theo đúng thứ tự.</summary>
    public async Task<Appointment> SetStatusAsync(Guid tenantId, Guid appointmentId, AppointmentStatus status, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var appointment = await RequireAsync(tenantId, appointmentId, tracked: true, cancellationToken);
        var allowed = appointment.Status switch
        {
            AppointmentStatus.Booked => status is AppointmentStatus.Confirmed or AppointmentStatus.Arrived or AppointmentStatus.InProgress or AppointmentStatus.Done or AppointmentStatus.NoShow,
            AppointmentStatus.Confirmed => status is AppointmentStatus.Arrived or AppointmentStatus.InProgress or AppointmentStatus.Done or AppointmentStatus.NoShow,
            AppointmentStatus.Arrived => status is AppointmentStatus.InProgress or AppointmentStatus.Done,
            AppointmentStatus.InProgress => status is AppointmentStatus.Done,
            AppointmentStatus.NoShow => status is AppointmentStatus.Arrived or AppointmentStatus.Done, // khách đến muộn
            _ => false,
        };
        if (!allowed)
        {
            throw new ConflictException($"Không chuyển được từ {appointment.Status} sang {status}.", "appointment_state");
        }

        var now = time.GetUtcNow();
        appointment.Status = status;
        switch (status)
        {
            case AppointmentStatus.Confirmed:
                appointment.CustomerConfirmedAt = now;
                break;
            case AppointmentStatus.Arrived:
                appointment.ArrivedAt ??= now;
                break;
            case AppointmentStatus.Done:
                appointment.ArrivedAt ??= now;
                appointment.DoneAt = now;
                break;
        }

        AddEvent(tenantId, appointment.Id, status == AppointmentStatus.Confirmed ? "confirmed" : "status_changed", actor, new { status = status.ToString().ToLowerInvariant() });
        await Db.SaveChangesAsync(cancellationToken);
        return appointment;
    }

    public async Task<Appointment> AssignAsync(Guid tenantId, Guid appointmentId, Guid? staffMemberId, Guid? resourceId, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var appointment = await RequireAsync(tenantId, appointmentId, tracked: true, cancellationToken);
        if (staffMemberId is { } s && !await Db.StaffMembers.AnyAsync(m => m.TenantId == tenantId && m.Id == s, cancellationToken))
        {
            throw new NotFoundException();
        }

        if (resourceId is { } r && !await Db.Resources.AnyAsync(x => x.TenantId == tenantId && x.Id == r && x.BranchId == appointment.BranchId, cancellationToken))
        {
            throw new NotFoundException();
        }

        appointment.StaffMemberId = staffMemberId;
        appointment.ResourceId = resourceId;
        AddEvent(tenantId, appointment.Id, "assigned", actor, new { staff = staffMemberId, resource = resourceId });
        await Db.SaveChangesAsync(cancellationToken);
        return appointment;
    }

    public async Task UpdateNoteAsync(Guid tenantId, Guid appointmentId, string? note, Actor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var appointment = await RequireAsync(tenantId, appointmentId, tracked: true, cancellationToken);
        appointment.NoteEnc = string.IsNullOrWhiteSpace(note) ? null : encryptor.Encrypt(note.Trim());
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Lịch định kỳ: N buổi cách nhau X ngày từ buổi đầu; buổi nào kín thì báo lại, các buổi khác vẫn tạo.</summary>
    public async Task<IReadOnlyList<SeriesOutcome>> CreateSeriesAsync(Guid tenantId, AppointmentInput first, int intervalDays, int occurrences, Actor actor, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(first);
        if (intervalDays is < 1 or > 90 || occurrences is < 2 or > 52)
        {
            throw new InvalidInputException("Lịch định kỳ: cách 1–90 ngày, 2–52 buổi.");
        }

        var contact = await ResolveContactAsync(tenantId, first, cancellationToken);
        await Db.SaveChangesAsync(cancellationToken);
        var series = new AppointmentSeries { TenantId = tenantId, ContactId = contact.Id, IntervalDays = intervalDays, Occurrences = occurrences };
        Db.AppointmentSeries.Add(series);
        await Db.SaveChangesAsync(cancellationToken);

        var outcomes = new List<SeriesOutcome>();
        for (var i = 0; i < occurrences; i++)
        {
            var start = first.StartAt.AddDays((double)i * intervalDays);
            try
            {
                var appointment = await CreateAsync(tenantId, first with { ContactId = contact.Id, NewContact = null, StartAt = start }, actor, cancellationToken);
                appointment.SeriesId = series.Id;
                await Db.SaveChangesAsync(cancellationToken);
                outcomes.Add(new SeriesOutcome(start, appointment, null));
            }
            catch (ConflictException ex)
            {
                outcomes.Add(new SeriesOutcome(start, null, ex.Code));
            }
            catch (InvalidInputException ex)
            {
                outcomes.Add(new SeriesOutcome(start, null, ex.Message));
            }
        }

        return outcomes;
    }

    // ---- Hàng chờ ----

    public async Task<WaitlistEntry> AddWaitlistAsync(Guid tenantId, Guid branchId, Guid contactId, Guid catalogItemId, Guid? staffMemberId, DateTimeOffset from, DateTimeOffset to, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        await branches.RequireAsync(tenantId, branchId, cancellationToken);
        if (from >= to || !await Db.Contacts.AnyAsync(c => c.TenantId == tenantId && c.Id == contactId, cancellationToken)
            || !await Db.CatalogItems.AnyAsync(c => c.TenantId == tenantId && c.Id == catalogItemId, cancellationToken))
        {
            throw new InvalidInputException("Hàng chờ cần khách, dịch vụ và khoảng thời gian hợp lệ.");
        }

        var entry = new WaitlistEntry
        {
            TenantId = tenantId,
            BranchId = branchId,
            ContactId = contactId,
            CatalogItemId = catalogItemId,
            StaffMemberId = staffMemberId,
            PreferredFrom = from.ToUniversalTime(),
            PreferredTo = to.ToUniversalTime(),
            NoteEnc = string.IsNullOrWhiteSpace(note) ? null : encryptor.Encrypt(note.Trim()),
        };
        Db.WaitlistEntries.Add(entry);
        await Db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyList<(WaitlistEntry Entry, string ContactName, string ServiceName)>> ListWaitlistAsync(Guid tenantId, Guid? branchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.WaitlistEntries.AsNoTracking().Where(w => w.TenantId == tenantId && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered));
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(w => w.BranchId == b);
        }

        var entries = await query.OrderBy(w => w.PreferredFrom).ToListAsync(cancellationToken);
        var contactIds = entries.Select(e => e.ContactId).Distinct().ToList();
        var contacts = await Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).ToListAsync(cancellationToken);
        var itemIds = entries.Select(e => e.CatalogItemId).Distinct().ToList();
        var items = await Db.CatalogItems.AsNoTracking().Where(c => c.TenantId == tenantId && itemIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return entries.Select(e => (e, ContactName(contacts.FirstOrDefault(c => c.Id == e.ContactId)), items.GetValueOrDefault(e.CatalogItemId, "?"))).ToList();
    }

    public async Task SetWaitlistStatusAsync(Guid tenantId, Guid entryId, WaitlistStatus status, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var entry = await Db.WaitlistEntries.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Id == entryId, cancellationToken) ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && entry.BranchId != only)
        {
            throw new NotFoundException();
        }

        entry.Status = status;
        await Db.SaveChangesAsync(cancellationToken);
    }

    // ---- Helpers ----

    internal async Task<Appointment> RequireAsync(Guid tenantId, Guid appointmentId, bool tracked, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = tracked ? Db.Appointments : Db.Appointments.AsNoTracking();
        var appointment = await query.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == appointmentId, cancellationToken) ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && appointment.BranchId != only)
        {
            throw new NotFoundException();
        }

        return appointment;
    }

    public async Task<IReadOnlyList<AppointmentView>> ViewsAsync(Guid tenantId, IReadOnlyList<Appointment> list, bool includeBrief, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
        {
            return [];
        }

        var ids = list.Select(a => a.Id).ToList();
        var items = await Db.AppointmentItems.AsNoTracking().Where(i => i.TenantId == tenantId && ids.Contains(i.AppointmentId)).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var contactIds = list.Select(a => a.ContactId).Distinct().ToList();
        var contacts = await Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var staffIds = list.Where(a => a.StaffMemberId != null).Select(a => a.StaffMemberId!.Value).Distinct().ToList();
        var staffNames = await Db.StaffMembers.AsNoTracking().Where(s => s.TenantId == tenantId && staffIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.DisplayName, cancellationToken);
        var branchIds = list.Select(a => a.BranchId).Distinct().ToList();
        var branchNames = await Db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId && branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        return list.Select(a =>
        {
            var contact = contacts.GetValueOrDefault(a.ContactId);
            return new AppointmentView(
                a,
                items.Where(i => i.AppointmentId == a.Id).ToList(),
                ContactName(contact),
                contact?.PhoneEnc is null ? null : PhoneNumber.Mask(encryptor.Decrypt(contact.PhoneEnc)),
                contact?.Salutation,
                a.StaffMemberId is { } s ? staffNames.GetValueOrDefault(s) : null,
                branchNames.GetValueOrDefault(a.BranchId, ""),
                a.NoteEnc is null ? null : encryptor.Decrypt(a.NoteEnc),
                includeBrief && a.AiBriefEnc is not null ? encryptor.Decrypt(a.AiBriefEnc) : null);
        }).ToList();
    }

    private string ContactName(Contact? contact) =>
        contact is null ? "Khách" : ContactView.Name(contact, ContactView.LeadFields(contact, encryptor));

    private async Task<Contact> ResolveContactAsync(Guid tenantId, AppointmentInput input, CancellationToken cancellationToken)
    {
        if (input.ContactId is { } id)
        {
            return await Db.Contacts.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, cancellationToken) ?? throw new NotFoundException();
        }

        if (input.NewContact is not { } fresh || string.IsNullOrWhiteSpace(fresh.Name))
        {
            throw new InvalidInputException("Cần chọn khách hoặc nhập tên khách mới.");
        }

        var phone = PhoneNumber.Normalize(fresh.Phone);
        if (!string.IsNullOrWhiteSpace(fresh.Phone) && phone is null)
        {
            throw new InvalidInputException("Số điện thoại không hợp lệ (di động 10 số).");
        }

        var hash = phone is null ? null : phoneHasher.Hash(phone);
        if (hash is not null && await Db.Contacts.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.PhoneHash == hash, cancellationToken) is { } existing)
        {
            return existing;
        }

        var contact = new Contact
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Channel = ChannelKind.Phone,
            ExternalUserId = hash ?? $"manual:{Guid.CreateVersion7():N}",
            DisplayName = fresh.Name.Trim(),
            PhoneEnc = phone is null ? null : encryptor.Encrypt(phone),
            PhoneHash = hash,
            Salutation = CustomerSalutation.IsAllowed(fresh.Salutation) ? fresh.Salutation : null,
            SalutationSource = CustomerSalutation.IsAllowed(fresh.Salutation) ? CustomerSalutation.SourceStaff : null,
        };
        Db.Contacts.Add(contact);
        return contact;
    }

    private void AddItems(Guid tenantId, Appointment appointment, IReadOnlyList<CatalogItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            Db.AppointmentItems.Add(new AppointmentItem
            {
                TenantId = tenantId,
                AppointmentId = appointment.Id,
                CatalogItemId = items[i].Id,
                Name = items[i].Name,
                DurationMinutes = items[i].DurationMinutes ?? 0,
                PriceSnapshot = items[i].Price,
                Position = i,
            });
        }
    }

    internal void AddEvent(Guid tenantId, Guid appointmentId, string kind, Actor actor, object? data)
    {
        Db.AppointmentEvents.Add(new AppointmentEvent
        {
            TenantId = tenantId,
            AppointmentId = appointmentId,
            Kind = kind,
            Actor = actor.Kind,
            ByUserId = actor.UserId,
            DataJson = data is null ? null : JsonSerializer.Serialize(data),
            At = time.GetUtcNow(),
        });
    }

    private void ValidateStart(DateTimeOffset startAt, CrmSettings settings, TimeZoneInfo zone, bool allowPast)
    {
        var now = time.GetUtcNow();
        if (!allowPast && startAt < now.AddMinutes(settings.BookingLeadMinutes))
        {
            throw new ConflictException($"Cần đặt trước ít nhất {settings.BookingLeadMinutes} phút.", "too_soon");
        }

        if (CrmClock.LocalDate(startAt, zone) > CrmClock.LocalDate(now, zone).AddDays(settings.MaxBookingDaysAhead))
        {
            throw new ConflictException($"Chỉ đặt trước tối đa {settings.MaxBookingDaysAhead} ngày.", "too_far");
        }
    }

    /// <summary>Khách tự dời/hủy qua chat phải trước giờ hẹn theo chính sách; nhân viên/hệ thống thì không giới hạn.</summary>
    private void EnsureCustomerLead(Appointment appointment, Actor actor, CrmSettings settings)
    {
        if (actor.Kind is "customer" or "bot" && appointment.StartAt - time.GetUtcNow() < TimeSpan.FromHours(settings.CancelLeadHours))
        {
            throw new ConflictException($"Đổi/hủy lịch cần báo trước {settings.CancelLeadHours} giờ.", "cancel_too_late");
        }
    }

    /// <summary>Khóa theo (tenant, chi nhánh, ngày) trong transaction → hai lần đặt cùng lúc được xếp tuần tự.</summary>
    private async Task LockDayAsync(Guid tenantId, Guid branchId, DateOnly date, CancellationToken cancellationToken)
    {
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}:{branchId:N}:{date:yyyyMMdd}")), 0);
        await Db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    /// <summary>UTC, bỏ giây/mili-giây: lịch luôn tròn phút để so khớp khung giờ.</summary>
    private static DateTimeOffset ToMinute(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    public static string DescribeItems(IEnumerable<AppointmentItem> items) => string.Join(" + ", items.Select(i => i.Name));

    public static string FormatMinutes(int minutes) => minutes.ToString(CultureInfo.InvariantCulture);
}
