using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Staffing;

/// <summary>Chấm công (CRM C5): nhân viên gắn tài khoản tự vào/ra ca; quản lý (manage_staff) ghi/sửa cho người khác.</summary>
public sealed class AttendanceService(AppDbContext db, ITenantContext tenantContext, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    /// <summary>Hồ sơ nhân viên gắn với tài khoản đang đăng nhập (null nếu chưa gắn).</summary>
    public async Task<StaffMember?> MeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return _caller.UserId is { } uid
            ? await Db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.UserId == uid, cancellationToken)
            : null;
    }

    public async Task<Attendance?> OpenAsync(Guid tenantId, Guid staffMemberId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Attendance.AsNoTracking().Where(a => a.TenantId == tenantId && a.StaffMemberId == staffMemberId && a.ClockOutAt == null)
            .OrderByDescending(a => a.ClockInAt).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Attendance> ClockInAsync(Guid tenantId, Guid staffMemberId, Guid? branchId, bool byManager, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var staff = await Db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == staffMemberId, cancellationToken) ?? throw new NotFoundException();
        if (await OpenAsync(tenantId, staffMemberId, cancellationToken) is not null)
        {
            throw new ConflictException("Đang trong ca, hãy ra ca trước.", "attendance_open");
        }

        var attendance = new Attendance
        {
            TenantId = tenantId,
            StaffMemberId = staffMemberId,
            BranchId = branchId ?? staff.BranchId,
            ClockInAt = time.GetUtcNow(),
            Source = byManager ? "manager" : "self",
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            ByUserId = _caller.UserId,
        };
        Db.Attendance.Add(attendance);
        await Db.SaveChangesAsync(cancellationToken);
        return attendance;
    }

    public async Task<Attendance> ClockOutAsync(Guid tenantId, Guid staffMemberId, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var open = await Db.Attendance.Where(a => a.TenantId == tenantId && a.StaffMemberId == staffMemberId && a.ClockOutAt == null)
            .OrderByDescending(a => a.ClockInAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new ConflictException("Chưa vào ca.", "attendance_closed");
        open.ClockOutAt = time.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(note))
        {
            open.Note = note.Trim();
        }

        await Db.SaveChangesAsync(cancellationToken);
        return open;
    }

    /// <summary>Quản lý sửa giờ vào/ra (sai sót).</summary>
    public async Task<Attendance> EditAsync(Guid tenantId, Guid attendanceId, DateTimeOffset clockIn, DateTimeOffset? clockOut, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (clockOut is { } o && o <= clockIn)
        {
            throw new InvalidInputException("Giờ ra phải sau giờ vào.");
        }

        var attendance = await Db.Attendance.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == attendanceId, cancellationToken) ?? throw new NotFoundException();
        attendance.ClockInAt = clockIn.ToUniversalTime();
        attendance.ClockOutAt = clockOut?.ToUniversalTime();
        attendance.Note = string.IsNullOrWhiteSpace(note) ? attendance.Note : note.Trim();
        attendance.Source = "manager";
        attendance.ByUserId = _caller.UserId;
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "attendance.edited", Target = attendance.Id.ToString() });
        await Db.SaveChangesAsync(cancellationToken);
        return attendance;
    }

    public async Task<IReadOnlyList<Attendance>> ListAsync(Guid tenantId, Guid? staffMemberId, Guid? branchId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Attendance.AsNoTracking().Where(a => a.TenantId == tenantId && a.ClockInAt >= from.ToUniversalTime() && a.ClockInAt < to.ToUniversalTime());
        if (staffMemberId is { } s)
        {
            query = query.Where(a => a.StaffMemberId == s);
        }

        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(a => a.BranchId == b);
        }

        return await query.OrderByDescending(a => a.ClockInAt).Take(1000).ToListAsync(cancellationToken);
    }
}
