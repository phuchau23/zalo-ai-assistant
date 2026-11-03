using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Staffing;

/// <param name="Revenue">Doanh thu ghi nhận cho người thực hiện (VNĐ).</param>
/// <param name="ReturningCustomers">Khách trong kỳ đã từng làm với nhân viên này trước kỳ.</param>
/// <param name="HoursWorked">Giờ chấm công trong kỳ (0 nếu không chấm).</param>
public sealed record StaffPerformanceRow(
    Guid StaffMemberId,
    string StaffName,
    Guid BranchId,
    int AppointmentsDone,
    int NoShows,
    long Revenue,
    long Commission,
    int Customers,
    int ReturningCustomers,
    double HoursWorked);

/// <summary>Hiệu suất nhân viên theo khoảng thời gian (CRM C5): lịch hoàn thành, khách, doanh thu, hoa hồng, giờ làm.</summary>
public sealed class PerformanceService(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<StaffPerformanceRow>> ReportAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, Guid? branchId, Guid? onlyStaffId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        var staffQuery = Db.StaffMembers.AsNoTracking().Where(s => s.TenantId == tenantId);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            staffQuery = staffQuery.Where(s => s.BranchId == b);
        }

        if (onlyStaffId is { } only)
        {
            staffQuery = staffQuery.Where(s => s.Id == only);
        }

        var staff = await staffQuery.OrderBy(s => s.DisplayName).ToListAsync(cancellationToken);
        var ids = staff.Select(s => s.Id).ToList();

        var appointments = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.StaffMemberId != null && ids.Contains(a.StaffMemberId.Value) && a.StartAt >= from && a.StartAt < to
                        && (a.Status == AppointmentStatus.Done || a.Status == AppointmentStatus.NoShow))
            .Select(a => new { StaffId = a.StaffMemberId!.Value, a.ContactId, a.Status })
            .ToListAsync(cancellationToken);
        var earlier = await Db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.StaffMemberId != null && ids.Contains(a.StaffMemberId.Value) && a.StartAt < from && a.Status == AppointmentStatus.Done)
            .Select(a => new { StaffId = a.StaffMemberId!.Value, a.ContactId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var revenue = await Db.RevenueEntries.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.StaffMemberId != null && ids.Contains(r.StaffMemberId.Value) && r.At >= from && r.At < to)
            .GroupBy(r => r.StaffMemberId!.Value)
            .Select(g => new { StaffId = g.Key, Amount = g.Sum(r => r.Amount) })
            .ToDictionaryAsync(g => g.StaffId, g => g.Amount, cancellationToken);
        var commission = await Db.CommissionEntries.AsNoTracking()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.StaffMemberId) && c.At >= from && c.At < to && c.RevertedAt == null)
            .GroupBy(c => c.StaffMemberId)
            .Select(g => new { StaffId = g.Key, Amount = g.Sum(c => c.Amount) })
            .ToDictionaryAsync(g => g.StaffId, g => g.Amount, cancellationToken);
        var attendance = await Db.Attendance.AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.StaffMemberId) && a.ClockInAt >= from && a.ClockInAt < to && a.ClockOutAt != null)
            .Select(a => new { a.StaffMemberId, a.ClockInAt, a.ClockOutAt })
            .ToListAsync(cancellationToken);

        return staff.Select(s =>
        {
            var mine = appointments.Where(a => a.StaffId == s.Id).ToList();
            var done = mine.Where(a => a.Status == AppointmentStatus.Done).ToList();
            var customers = done.Select(a => a.ContactId).Distinct().ToList();
            var before = earlier.Where(e => e.StaffId == s.Id).Select(e => e.ContactId).ToHashSet();
            return new StaffPerformanceRow(
                s.Id,
                s.DisplayName,
                s.BranchId,
                done.Count,
                mine.Count(a => a.Status == AppointmentStatus.NoShow),
                revenue.GetValueOrDefault(s.Id),
                commission.GetValueOrDefault(s.Id),
                customers.Count,
                customers.Count(before.Contains),
                Math.Round(attendance.Where(a => a.StaffMemberId == s.Id).Sum(a => (a.ClockOutAt!.Value - a.ClockInAt).TotalHours), 1));
        }).ToList();
    }
}
