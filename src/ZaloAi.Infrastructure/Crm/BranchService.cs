using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

public sealed record BranchInput(string Code, string Name, string? Address, string? Phone);

public sealed record BranchHourInput(DayOfWeek DayOfWeek, TimeOnly OpenTime, TimeOnly CloseTime);

public sealed record BranchDetail(Branch Branch, IReadOnlyList<BranchHour> Hours, IReadOnlyList<BranchClosure> Closures);

/// <summary>
/// Chi nhánh (CRM C1). Tenant nào cũng có một chi nhánh mặc định, tạo lười lần đầu cần tới (tenant cũ không cần migration dữ liệu).
/// Nhân viên bị giới hạn chi nhánh (<see cref="ITenantContext.BranchId"/>) chỉ thấy chi nhánh đó trong danh sách.
/// </summary>
public sealed class BranchService(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public const string DefaultCode = "CN1";

    private readonly ITenantContext _caller = tenantContext;

    private static readonly BranchHourInput[] DefaultHours =
        Enumerable.Range(0, 7).Select(d => new BranchHourInput((DayOfWeek)d, new TimeOnly(8, 0), new TimeOnly(20, 0))).ToArray();

    /// <summary>Danh sách chi nhánh người gọi được xem. Tạo chi nhánh mặc định nếu tenant chưa có.</summary>
    public async Task<IReadOnlyList<Branch>> ListAsync(Guid tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        await EnsureDefaultAsync(tenantId, cancellationToken);
        var query = Db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId);
        if (!includeInactive)
        {
            query = query.Where(b => b.Status == BranchStatus.Active);
        }

        if (_caller.BranchId is { } only)
        {
            query = query.Where(b => b.Id == only);
        }

        return await query.OrderBy(b => b.SortOrder).ThenBy(b => b.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<BranchDetail> GetAsync(Guid tenantId, Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await RequireAsync(tenantId, branchId, cancellationToken);
        var hours = await Db.BranchHours.AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.BranchId == branchId)
            .OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpenTime)
            .ToListAsync(cancellationToken);
        var closures = await Db.BranchClosures.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.BranchId == branchId)
            .OrderBy(c => c.Date)
            .ToListAsync(cancellationToken);
        return new BranchDetail(branch, hours, closures);
    }

    /// <summary>Chi nhánh mặc định của tenant (tạo nếu chưa có). Dùng khi tạo lịch/hóa đơn mà không chỉ rõ chi nhánh.</summary>
    public async Task<Branch> EnsureDefaultAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var existing = await Db.Branches.Where(b => b.TenantId == tenantId).OrderByDescending(b => b.IsDefault).ThenBy(b => b.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var tenant = await Db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, cancellationToken);
        var branch = new Branch { TenantId = tenantId, Code = DefaultCode, Name = tenant.Name, IsDefault = true };
        Db.Branches.Add(branch);
        AddHours(tenantId, branch.Id, DefaultHours);
        await Db.SaveChangesAsync(cancellationToken);
        return branch;
    }

    public async Task<Branch> CreateAsync(Guid tenantId, BranchInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        var code = NormalizeCode(input.Code);
        if (await Db.Branches.AnyAsync(b => b.TenantId == tenantId && b.Code == code, cancellationToken))
        {
            throw new ConflictException($"Mã chi nhánh {code} đã có.", "duplicate_code");
        }

        var isFirst = !await Db.Branches.AnyAsync(b => b.TenantId == tenantId, cancellationToken);
        var branch = new Branch
        {
            TenantId = tenantId,
            Code = code,
            Name = input.Name.Trim(),
            Address = Trim(input.Address),
            Phone = Trim(input.Phone),
            IsDefault = isFirst,
            SortOrder = await Db.Branches.CountAsync(b => b.TenantId == tenantId, cancellationToken),
        };
        Db.Branches.Add(branch);
        AddHours(tenantId, branch.Id, DefaultHours);
        Audit(tenantId, "branch.created", branch.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return branch;
    }

    public async Task<Branch> UpdateAsync(Guid tenantId, Guid branchId, BranchInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var branch = await RequireAsync(tenantId, branchId, cancellationToken);
        var code = NormalizeCode(input.Code);
        if (code != branch.Code && await Db.Branches.AnyAsync(b => b.TenantId == tenantId && b.Code == code, cancellationToken))
        {
            throw new ConflictException($"Mã chi nhánh {code} đã có.", "duplicate_code");
        }

        branch.Code = code;
        branch.Name = input.Name.Trim();
        branch.Address = Trim(input.Address);
        branch.Phone = Trim(input.Phone);
        Audit(tenantId, "branch.updated", branch.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return branch;
    }

    public async Task SetDefaultAsync(Guid tenantId, Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await RequireAsync(tenantId, branchId, cancellationToken);
        if (branch.Status != BranchStatus.Active)
        {
            throw new ConflictException("Chi nhánh đang ngừng hoạt động không thể làm mặc định.", "branch_inactive");
        }

        await Db.Branches.Where(b => b.TenantId == tenantId && b.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false), cancellationToken);
        branch.IsDefault = true;
        Audit(tenantId, "branch.default_changed", branch.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid tenantId, Guid branchId, BranchStatus status, CancellationToken cancellationToken)
    {
        var branch = await RequireAsync(tenantId, branchId, cancellationToken);
        if (status == BranchStatus.Inactive && branch.IsDefault)
        {
            throw new ConflictException("Đặt chi nhánh khác làm mặc định trước khi ngừng chi nhánh này.", "default_branch");
        }

        branch.Status = status;
        Audit(tenantId, status == BranchStatus.Active ? "branch.activated" : "branch.deactivated", branch.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Thay toàn bộ giờ mở cửa. Khung giờ phải có mở &lt; đóng; cùng thứ không chồng nhau.</summary>
    public async Task SetHoursAsync(Guid tenantId, Guid branchId, IReadOnlyList<BranchHourInput> hours, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hours);
        await RequireAsync(tenantId, branchId, cancellationToken);
        ValidateHours(hours);
        await Db.BranchHours.Where(h => h.TenantId == tenantId && h.BranchId == branchId).ExecuteDeleteAsync(cancellationToken);
        AddHours(tenantId, branchId, hours);
        Audit(tenantId, "branch.hours_updated", branchId.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BranchClosure> AddClosureAsync(Guid tenantId, Guid branchId, DateOnly date, string? reason, CancellationToken cancellationToken)
    {
        await RequireAsync(tenantId, branchId, cancellationToken);
        if (await Db.BranchClosures.AnyAsync(c => c.TenantId == tenantId && c.BranchId == branchId && c.Date == date, cancellationToken))
        {
            throw new ConflictException("Ngày này đã có trong danh sách nghỉ.", "duplicate_closure");
        }

        var closure = new BranchClosure { TenantId = tenantId, BranchId = branchId, Date = date, Reason = Trim(reason) };
        Db.BranchClosures.Add(closure);
        await Db.SaveChangesAsync(cancellationToken);
        return closure;
    }

    public async Task RemoveClosureAsync(Guid tenantId, Guid branchId, Guid closureId, CancellationToken cancellationToken)
    {
        await RequireAsync(tenantId, branchId, cancellationToken);
        var removed = await Db.BranchClosures
            .Where(c => c.TenantId == tenantId && c.BranchId == branchId && c.Id == closureId)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
        {
            throw new NotFoundException();
        }
    }

    /// <summary>Giờ mở cửa của chi nhánh trong một ngày (đã trừ ngày nghỉ) — cho tính chỗ trống lịch (C2).</summary>
    public async Task<IReadOnlyList<(TimeOnly Open, TimeOnly Close)>> OpenWindowsAsync(Guid tenantId, Guid branchId, DateOnly date, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (await Db.BranchClosures.AnyAsync(c => c.TenantId == tenantId && c.BranchId == branchId && c.Date == date, cancellationToken))
        {
            return [];
        }

        var day = date.DayOfWeek;
        return await Db.BranchHours.AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.BranchId == branchId && h.DayOfWeek == day)
            .OrderBy(h => h.OpenTime)
            .Select(h => new ValueTuple<TimeOnly, TimeOnly>(h.OpenTime, h.CloseTime))
            .ToListAsync(cancellationToken);
    }

    internal async Task<Branch> RequireAsync(Guid tenantId, Guid branchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (_caller.BranchId is { } only && only != branchId)
        {
            throw new NotFoundException();
        }

        return await Db.Branches.FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == branchId, cancellationToken)
            ?? throw new NotFoundException();
    }

    private void AddHours(Guid tenantId, Guid branchId, IEnumerable<BranchHourInput> hours)
    {
        foreach (var h in hours)
        {
            Db.BranchHours.Add(new BranchHour { TenantId = tenantId, BranchId = branchId, DayOfWeek = h.DayOfWeek, OpenTime = h.OpenTime, CloseTime = h.CloseTime });
        }
    }

    private static void ValidateHours(IReadOnlyList<BranchHourInput> hours)
    {
        foreach (var group in hours.GroupBy(h => h.DayOfWeek))
        {
            BranchHourInput? previous = null;
            foreach (var h in group.OrderBy(h => h.OpenTime))
            {
                if (h.OpenTime >= h.CloseTime)
                {
                    throw new InvalidInputException("Giờ mở phải trước giờ đóng.");
                }

                if (previous is not null && h.OpenTime < previous.CloseTime)
                {
                    throw new InvalidInputException("Các khung giờ trong cùng một ngày không được chồng nhau.");
                }

                previous = h;
            }
        }
    }

    internal static string NormalizeCode(string raw) =>
        KnowledgeTemplate.NormalizeCode(raw) ?? throw new InvalidInputException("Mã chi nhánh chỉ gồm chữ in hoa không dấu, số, gạch ngang (2–50 ký tự).");

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });
}
