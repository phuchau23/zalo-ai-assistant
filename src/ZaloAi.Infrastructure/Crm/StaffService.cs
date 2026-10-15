using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

public sealed record StaffInput(Guid BranchId, string DisplayName, string? Title, string? Phone, string Color, bool Bookable, Guid? UserId);

public sealed record ShiftTemplateInput(DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed record ShiftInput(DateOnly Date, Guid? BranchId, TimeOnly StartTime, TimeOnly EndTime);

public sealed record TimeOffInput(DateTimeOffset FromAt, DateTimeOffset ToAt, string? Reason);

public sealed record StaffDetail(
    StaffMember Staff,
    string? Phone,
    IReadOnlyList<Guid> ServiceIds,
    IReadOnlyList<StaffShiftTemplate> ShiftTemplates,
    IReadOnlyList<StaffTimeOff> TimeOff);

/// <summary>Khoảng làm việc thực tế của một nhân viên trong một ngày (ca tay ghi đè ca cố định, đã trừ nghỉ phép ở C2).</summary>
public sealed record WorkWindow(Guid StaffMemberId, Guid BranchId, TimeOnly Start, TimeOnly End);

/// <summary>
/// Nhân viên / kỹ thuật viên (CRM C1): hồ sơ, dịch vụ làm được, ca cố định, ca theo ngày, nghỉ phép. SĐT mã hóa.
/// Nhân viên bị giới hạn chi nhánh chỉ thấy nhân viên cùng chi nhánh.
/// </summary>
public sealed class StaffService(AppDbContext db, ITenantContext tenantContext, IFieldEncryptor encryptor) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<StaffMember>> ListAsync(Guid tenantId, Guid? branchId, bool includeInactive, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.StaffMembers.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (!includeInactive)
        {
            query = query.Where(s => s.Status == StaffStatus.Active);
        }

        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(s => s.BranchId == b);
        }

        return await query.OrderBy(s => s.SortOrder).ThenBy(s => s.DisplayName).ToListAsync(cancellationToken);
    }

    public async Task<StaffDetail> GetAsync(Guid tenantId, Guid staffId, CancellationToken cancellationToken)
    {
        var staff = await RequireAsync(tenantId, staffId, cancellationToken);
        var services = await Db.StaffSkills.AsNoTracking().Where(x => x.TenantId == tenantId && x.StaffMemberId == staffId)
            .Select(x => x.CatalogItemId).ToListAsync(cancellationToken);
        var templates = await Db.StaffShiftTemplates.AsNoTracking().Where(t => t.TenantId == tenantId && t.StaffMemberId == staffId)
            .OrderBy(t => t.DayOfWeek).ThenBy(t => t.StartTime).ToListAsync(cancellationToken);
        var timeOff = await Db.StaffTimeOffs.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.StaffMemberId == staffId && t.ToAt >= DateTimeOffset.UtcNow.AddDays(-30))
            .OrderBy(t => t.FromAt).ToListAsync(cancellationToken);
        return new StaffDetail(staff, staff.PhoneEnc is null ? null : encryptor.Decrypt(staff.PhoneEnc), services, templates, timeOff);
    }

    public async Task<StaffMember> CreateAsync(Guid tenantId, StaffInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        await RequireBranchAsync(tenantId, input.BranchId, cancellationToken);
        await EnsureUserFreeAsync(tenantId, input.UserId, null, cancellationToken);
        var staff = new StaffMember
        {
            TenantId = tenantId,
            BranchId = input.BranchId,
            DisplayName = "",
            SortOrder = await Db.StaffMembers.CountAsync(s => s.TenantId == tenantId, cancellationToken),
        };
        Apply(staff, input);
        Db.StaffMembers.Add(staff);
        Audit(tenantId, "staff.created", staff.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return staff;
    }

    public async Task<StaffMember> UpdateAsync(Guid tenantId, Guid staffId, StaffInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var staff = await RequireAsync(tenantId, staffId, cancellationToken);
        await RequireBranchAsync(tenantId, input.BranchId, cancellationToken);
        await EnsureUserFreeAsync(tenantId, input.UserId, staffId, cancellationToken);
        staff.BranchId = input.BranchId;
        Apply(staff, input);
        Audit(tenantId, "staff.updated", staff.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return staff;
    }

    public async Task SetStatusAsync(Guid tenantId, Guid staffId, StaffStatus status, CancellationToken cancellationToken)
    {
        var staff = await RequireAsync(tenantId, staffId, cancellationToken);
        staff.Status = status;
        Audit(tenantId, status == StaffStatus.Active ? "staff.activated" : "staff.deactivated", staff.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Dịch vụ làm được; rỗng = mọi dịch vụ.</summary>
    public async Task SetServicesAsync(Guid tenantId, Guid staffId, IReadOnlyList<Guid> serviceIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        await RequireAsync(tenantId, staffId, cancellationToken);
        var distinct = serviceIds.Distinct().ToList();
        var known = await Db.CatalogItems.Where(c => c.TenantId == tenantId && distinct.Contains(c.Id) && c.Kind == CatalogKind.Service)
            .Select(c => c.Id).ToListAsync(cancellationToken);
        if (known.Count != distinct.Count)
        {
            throw new NotFoundException();
        }

        await Db.StaffSkills.Where(x => x.TenantId == tenantId && x.StaffMemberId == staffId).ExecuteDeleteAsync(cancellationToken);
        foreach (var id in known)
        {
            Db.StaffSkills.Add(new StaffSkill { TenantId = tenantId, StaffMemberId = staffId, CatalogItemId = id });
        }

        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetShiftTemplatesAsync(Guid tenantId, Guid staffId, IReadOnlyList<ShiftTemplateInput> templates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templates);
        await RequireAsync(tenantId, staffId, cancellationToken);
        ValidateWindows(templates.Select(t => ((int)t.DayOfWeek, t.StartTime, t.EndTime)));
        await Db.StaffShiftTemplates.Where(t => t.TenantId == tenantId && t.StaffMemberId == staffId).ExecuteDeleteAsync(cancellationToken);
        foreach (var t in templates)
        {
            Db.StaffShiftTemplates.Add(new StaffShiftTemplate { TenantId = tenantId, StaffMemberId = staffId, DayOfWeek = t.DayOfWeek, StartTime = t.StartTime, EndTime = t.EndTime });
        }

        Audit(tenantId, "staff.shift_templates_updated", staffId.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Ca theo ngày (ghi đè ca cố định của ngày đó). Danh sách rỗng cho một ngày = nghỉ ngày đó.</summary>
    public async Task SetShiftsForDateAsync(Guid tenantId, Guid staffId, DateOnly date, IReadOnlyList<ShiftInput> shifts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        var staff = await RequireAsync(tenantId, staffId, cancellationToken);
        if (shifts.Any(s => s.Date != date))
        {
            throw new InvalidInputException("Mọi ca phải cùng ngày.");
        }

        ValidateWindows(shifts.Select(s => (0, s.StartTime, s.EndTime)));
        await Db.StaffShifts.Where(s => s.TenantId == tenantId && s.StaffMemberId == staffId && s.Date == date).ExecuteDeleteAsync(cancellationToken);
        foreach (var s in shifts)
        {
            var branchId = s.BranchId ?? staff.BranchId;
            await RequireBranchAsync(tenantId, branchId, cancellationToken);
            Db.StaffShifts.Add(new StaffShift { TenantId = tenantId, StaffMemberId = staffId, BranchId = branchId, Date = date, StartTime = s.StartTime, EndTime = s.EndTime, Source = ShiftSource.Manual });
        }

        // Ngày nghỉ (không ca) cũng cần một dòng để biết "đã ghi đè": lưu ca rỗng 00:00–00:00 không được, nên dùng nghỉ phép cả ngày.
        if (shifts.Count == 0)
        {
            var dayStart = new DateTimeOffset(date, TimeOnly.MinValue, TimeSpan.Zero);
            Db.StaffTimeOffs.Add(new StaffTimeOff { TenantId = tenantId, StaffMemberId = staffId, FromAt = dayStart, ToAt = dayStart.AddDays(1), Reason = "Nghỉ" });
        }

        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StaffShift>> ListShiftsAsync(Guid tenantId, Guid? branchId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.StaffShifts.AsNoTracking().Where(s => s.TenantId == tenantId && s.Date >= from && s.Date <= to);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(s => s.BranchId == b);
        }

        return await query.OrderBy(s => s.Date).ThenBy(s => s.StartTime).ToListAsync(cancellationToken);
    }

    public async Task<StaffTimeOff> AddTimeOffAsync(Guid tenantId, Guid staffId, TimeOffInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await RequireAsync(tenantId, staffId, cancellationToken);
        if (input.FromAt >= input.ToAt)
        {
            throw new InvalidInputException("Thời điểm bắt đầu phải trước kết thúc.");
        }

        var timeOff = new StaffTimeOff { TenantId = tenantId, StaffMemberId = staffId, FromAt = input.FromAt.ToUniversalTime(), ToAt = input.ToAt.ToUniversalTime(), Reason = Trim(input.Reason) };
        Db.StaffTimeOffs.Add(timeOff);
        await Db.SaveChangesAsync(cancellationToken);
        return timeOff;
    }

    public async Task RemoveTimeOffAsync(Guid tenantId, Guid staffId, Guid timeOffId, CancellationToken cancellationToken)
    {
        await RequireAsync(tenantId, staffId, cancellationToken);
        var removed = await Db.StaffTimeOffs.Where(t => t.TenantId == tenantId && t.StaffMemberId == staffId && t.Id == timeOffId).ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
        {
            throw new NotFoundException();
        }
    }

    /// <summary>
    /// Giờ làm thực tế của các nhân viên (đang hoạt động, nhận lịch) của một chi nhánh trong một ngày: ca tay nếu có, không thì ca cố định;
    /// trừ nghỉ phép trùm cả ngày. Nghỉ phép một phần ngày được C2 trừ khi tính chỗ trống.
    /// </summary>
    public async Task<IReadOnlyList<WorkWindow>> WorkWindowsAsync(Guid tenantId, Guid branchId, DateOnly date, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var staff = await Db.StaffMembers.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status == StaffStatus.Active && s.Bookable)
            .Select(s => new { s.Id, s.BranchId }).ToListAsync(cancellationToken);
        var ids = staff.Select(s => s.Id).ToList();
        var manual = await Db.StaffShifts.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Date == date && ids.Contains(s.StaffMemberId)).ToListAsync(cancellationToken);
        var templates = await Db.StaffShiftTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.DayOfWeek == date.DayOfWeek && ids.Contains(t.StaffMemberId)).ToListAsync(cancellationToken);
        var dayStart = new DateTimeOffset(date, TimeOnly.MinValue, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var offAllDay = await Db.StaffTimeOffs.AsNoTracking()
            .Where(t => t.TenantId == tenantId && ids.Contains(t.StaffMemberId) && t.FromAt <= dayStart && t.ToAt >= dayEnd)
            .Select(t => t.StaffMemberId).ToListAsync(cancellationToken);

        var windows = new List<WorkWindow>();
        foreach (var s in staff.Where(s => !offAllDay.Contains(s.Id)))
        {
            var manualShifts = manual.Where(m => m.StaffMemberId == s.Id).ToList();
            if (manualShifts.Count > 0)
            {
                windows.AddRange(manualShifts.Where(m => m.BranchId == branchId).Select(m => new WorkWindow(s.Id, m.BranchId, m.StartTime, m.EndTime)));
            }
            else if (s.BranchId == branchId)
            {
                windows.AddRange(templates.Where(t => t.StaffMemberId == s.Id).Select(t => new WorkWindow(s.Id, branchId, t.StartTime, t.EndTime)));
            }
        }

        return windows;
    }

    internal async Task<StaffMember> RequireAsync(Guid tenantId, Guid staffId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var staff = await Db.StaffMembers.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == staffId, cancellationToken)
            ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && staff.BranchId != only)
        {
            throw new NotFoundException();
        }

        return staff;
    }

    private async Task RequireBranchAsync(Guid tenantId, Guid branchId, CancellationToken cancellationToken)
    {
        if (!await Db.Branches.AnyAsync(b => b.TenantId == tenantId && b.Id == branchId, cancellationToken))
        {
            throw new NotFoundException();
        }
    }

    private async Task EnsureUserFreeAsync(Guid tenantId, Guid? userId, Guid? exceptStaffId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return;
        }

        if (!await Db.Memberships.AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, cancellationToken))
        {
            throw new InvalidInputException("Tài khoản này không thuộc doanh nghiệp.");
        }

        if (await Db.StaffMembers.AnyAsync(s => s.TenantId == tenantId && s.UserId == userId && s.Id != exceptStaffId, cancellationToken))
        {
            throw new ConflictException("Tài khoản này đã gắn với một nhân viên khác.", "user_already_linked");
        }
    }

    private void Apply(StaffMember staff, StaffInput input)
    {
        staff.DisplayName = input.DisplayName.Trim();
        staff.Title = Trim(input.Title);
        staff.PhoneEnc = string.IsNullOrWhiteSpace(input.Phone) ? null : encryptor.Encrypt(input.Phone.Trim());
        staff.Color = IsColor(input.Color) ? input.Color : throw new InvalidInputException("Màu phải dạng #RRGGBB.");
        staff.Bookable = input.Bookable;
        staff.UserId = input.UserId;
    }

    private static bool IsColor(string value) =>
        value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private static void ValidateWindows(IEnumerable<(int Day, TimeOnly Start, TimeOnly End)> windows)
    {
        foreach (var group in windows.GroupBy(w => w.Day))
        {
            TimeOnly? previousEnd = null;
            foreach (var w in group.OrderBy(w => w.Start))
            {
                if (w.Start >= w.End)
                {
                    throw new InvalidInputException("Giờ bắt đầu ca phải trước giờ kết thúc.");
                }

                if (previousEnd is { } end && w.Start < end)
                {
                    throw new InvalidInputException("Các ca trong cùng ngày không được chồng nhau.");
                }

                previousEnd = w.End;
            }
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });
}
