using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

/// <param name="StartAt">UTC.</param>
public sealed record AvailableSlot(Guid? StaffMemberId, string? StaffName, DateTimeOffset StartAt, DateTimeOffset EndAt);

/// <param name="DurationMinutes">Tổng phút các dịch vụ (không gồm dọn dẹp).</param>
public sealed record ServiceRequirement(IReadOnlyList<CatalogItem> Items, int DurationMinutes, int BufferMinutes);

/// <summary>
/// Tính chỗ trống thật của một chi nhánh trong một ngày (CRM C2): giờ mở cửa + ngày nghỉ, ca nhân viên + nghỉ phép, dịch vụ nhân viên
/// làm được, lịch đã có (+ dọn dẹp), sức chứa giường/phòng. Bot và lễ tân cùng dùng → không bao giờ nhận trùng.
/// </summary>
public sealed class AvailabilityService(
    AppDbContext db,
    ITenantContext tenantContext,
    BranchService branches,
    StaffService staff,
    CrmSettingsRepository settingsRepository,
    TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Gom dịch vụ cần làm: thời lượng cộng dồn, dọn dẹp lấy lớn nhất (hoặc mặc định DN).</summary>
    public async Task<ServiceRequirement> RequirementAsync(Guid tenantId, IReadOnlyList<Guid> catalogItemIds, CrmSettings settings, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(catalogItemIds);
        ArgumentNullException.ThrowIfNull(settings);
        var ids = catalogItemIds.Distinct().ToList();
        var items = await Db.CatalogItems.AsNoTracking()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id) && c.IsActive && c.Kind != CatalogKind.Product)
            .ToListAsync(cancellationToken);
        if (items.Count != ids.Count || items.Count == 0)
        {
            throw new NotFoundException();
        }

        var ordered = ids.Select(id => items.First(i => i.Id == id)).ToList();
        var duration = ordered.Sum(i => i.DurationMinutes ?? 0);
        if (duration <= 0)
        {
            throw new InvalidInputException("Dịch vụ chưa có thời lượng nên chưa xếp lịch được — cập nhật ở Danh mục.");
        }

        var buffer = ordered.Max(i => i.BufferMinutes);
        return new ServiceRequirement(ordered, duration, buffer > 0 ? buffer : settings.DefaultBufferMinutes);
    }

    /// <summary>Mọi khung trống trong ngày (giờ địa phương), từng nhân viên. <paramref name="staffMemberId"/> = chỉ người đó.</summary>
    public async Task<IReadOnlyList<AvailableSlot>> SlotsAsync(
        Guid tenantId,
        Guid branchId,
        DateOnly date,
        ServiceRequirement requirement,
        Guid? staffMemberId,
        Guid? ignoreAppointmentId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(requirement);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var zone = CrmClock.Zone(settings.TimeZone);
        var now = time.GetUtcNow();
        var today = CrmClock.LocalDate(now, zone);
        if (date < today || date > today.AddDays(settings.MaxBookingDaysAhead))
        {
            return [];
        }

        var open = (await branches.OpenWindowsAsync(tenantId, branchId, date, cancellationToken))
            .Select(w => (SlotCalculator.Minutes(w.Open), SlotCalculator.Minutes(w.Close))).ToList();
        if (open.Count == 0)
        {
            return [];
        }

        var dayStart = CrmClock.FromLocal(date, TimeOnly.MinValue, zone);
        var dayEnd = dayStart.AddDays(1);

        // Nhân viên làm được MỌI dịch vụ yêu cầu (không khai dịch vụ = làm được tất cả).
        var serviceIds = requirement.Items.Select(i => i.Id).ToList();
        var windows = await staff.WorkWindowsAsync(tenantId, branchId, date, cancellationToken);
        var staffIds = windows.Select(w => w.StaffMemberId).Distinct().ToList();
        if (staffMemberId is { } only)
        {
            staffIds = staffIds.Where(id => id == only).ToList();
        }

        var skills = await Db.StaffSkills.AsNoTracking().Where(s => s.TenantId == tenantId && staffIds.Contains(s.StaffMemberId)).ToListAsync(cancellationToken);
        var capable = staffIds.Where(id =>
        {
            var mine = skills.Where(s => s.StaffMemberId == id).Select(s => s.CatalogItemId).ToHashSet();
            return mine.Count == 0 || serviceIds.All(mine.Contains);
        }).ToList();

        var appointments = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.BranchId == branchId && a.StartAt < dayEnd && a.EndAt > dayStart
                        && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow
                        && (ignoreAppointmentId == null || a.Id != ignoreAppointmentId))
            .Select(a => new { a.StaffMemberId, a.StartAt, a.EndAt, a.BufferMinutes })
            .ToListAsync(cancellationToken);
        var timeOff = await Db.StaffTimeOffs.AsNoTracking()
            .Where(t => t.TenantId == tenantId && capable.Contains(t.StaffMemberId) && t.FromAt < dayEnd && t.ToAt > dayStart)
            .ToListAsync(cancellationToken);

        BusyInterval ToBusy(DateTimeOffset from, DateTimeOffset to, int extra = 0) =>
            new(Math.Max(0, (int)(from - dayStart).TotalMinutes), Math.Min(1440, (int)(to - dayStart).TotalMinutes + extra));

        var branchBusy = appointments.Select(a => ToBusy(a.StartAt, a.EndAt, a.BufferMinutes)).ToList();
        var perStaff = capable.Select(id => new SlotCalculator.StaffAvailability(
            id,
            windows.Where(w => w.StaffMemberId == id).Select(w => (SlotCalculator.Minutes(w.Start), SlotCalculator.Minutes(w.End))).ToList(),
            appointments.Where(a => a.StaffMemberId == id).Select(a => ToBusy(a.StartAt, a.EndAt, a.BufferMinutes))
                .Concat(timeOff.Where(t => t.StaffMemberId == id).Select(t => ToBusy(t.FromAt, t.ToAt)))
                .ToList())).ToList();

        var capacity = await Db.Resources.CountAsync(r => r.TenantId == tenantId && r.BranchId == branchId && r.IsActive, cancellationToken);
        var earliest = date == today ? SlotCalculator.Minutes(CrmClock.LocalTime(now.AddMinutes(settings.BookingLeadMinutes), zone)) : 0;

        var slots = SlotCalculator.Compute(new SlotCalculator.Input(
            open, perStaff, branchBusy, capacity > 0 ? capacity : null, requirement.DurationMinutes, requirement.BufferMinutes, settings.SlotMinutes, earliest));

        var names = await Db.StaffMembers.AsNoTracking().Where(s => s.TenantId == tenantId && capable.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, cancellationToken);
        return slots.Select(s =>
        {
            var start = dayStart.AddMinutes(s.Start);
            return new AvailableSlot(s.StaffMemberId, s.StaffMemberId is { } id ? names.GetValueOrDefault(id) : null, start, start.AddMinutes(requirement.DurationMinutes));
        }).ToList();
    }

    /// <summary>Khung giờ đúng <paramref name="startAt"/> còn trống không; trả nhân viên được chọn (ít lịch nhất) hoặc null nếu kín.</summary>
    public async Task<AvailableSlot?> CheckAsync(
        Guid tenantId, Guid branchId, DateTimeOffset startAt, ServiceRequirement requirement, Guid? staffMemberId, Guid? ignoreAppointmentId, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var zone = CrmClock.Zone(settings.TimeZone);
        var date = CrmClock.LocalDate(startAt, zone);
        var slots = await SlotsAsync(tenantId, branchId, date, requirement, staffMemberId, ignoreAppointmentId, cancellationToken);
        var matching = slots.Where(s => s.StartAt == startAt).ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        var load = await LoadAsync(tenantId, date, zone, cancellationToken);
        return matching.OrderBy(s => s.StaffMemberId is { } id ? load.GetValueOrDefault(id) : 0).First();
    }

    /// <summary>Gợi ý tối đa <paramref name="count"/> giờ gần giờ khách muốn nhất (cùng ngày; không có → ngày kế tiếp có chỗ, tối đa 3 ngày).</summary>
    public async Task<IReadOnlyList<AvailableSlot>> SuggestAsync(
        Guid tenantId, Guid branchId, DateOnly date, TimeOnly? wanted, ServiceRequirement requirement, Guid? staffMemberId, int count, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < 4; offset++)
        {
            var day = date.AddDays(offset);
            var slots = await SlotsAsync(tenantId, branchId, day, requirement, staffMemberId, null, cancellationToken);
            if (slots.Count == 0)
            {
                continue;
            }

            var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
            var zone = CrmClock.Zone(settings.TimeZone);
            var target = wanted is { } w ? SlotCalculator.Minutes(w) : 9 * 60;
            var distinct = slots.GroupBy(s => s.StartAt).Select(g => g.First()).ToList();
            return distinct
                .OrderBy(s => Math.Abs(SlotCalculator.Minutes(CrmClock.LocalTime(s.StartAt, zone)) - target))
                .ThenBy(s => s.StartAt)
                .Take(count)
                .OrderBy(s => s.StartAt)
                .ToList();
        }

        return [];
    }

    private async Task<Dictionary<Guid, int>> LoadAsync(Guid tenantId, DateOnly date, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var dayStart = CrmClock.FromLocal(date, TimeOnly.MinValue, zone);
        var dayEnd = dayStart.AddDays(1);
        return await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.StaffMemberId != null && a.StartAt >= dayStart && a.StartAt < dayEnd
                        && a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow)
            .GroupBy(a => a.StaffMemberId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
    }
}
