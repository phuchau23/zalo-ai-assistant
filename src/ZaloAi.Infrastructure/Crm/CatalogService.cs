using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

public sealed record CatalogItemInput(
    CatalogKind Kind,
    string Code,
    string Name,
    string? GroupName,
    long? Price,
    string? PriceNote,
    int? DurationMinutes,
    int BufferMinutes,
    int? SessionCount,
    int? ValidityDays,
    string? Sku,
    string? Unit,
    long? CostPrice,
    bool TrackInventory);

public sealed record PackageContentInput(Guid ServiceId, int SessionCount);

public sealed record CatalogItemDetail(CatalogItem Item, IReadOnlyList<Guid> BranchIds, IReadOnlyList<PackageContent> Contents);

/// <summary>
/// Danh mục bán hàng (CRM C1). Mọi thay đổi giá/tên/thời lượng được đẩy sang kho kiến thức để bot nói đúng giá.
/// Không xóa cứng mục đã có (hóa đơn/lịch tham chiếu) — chỉ ngừng bán.
/// </summary>
public sealed class CatalogService(AppDbContext db, ITenantContext tenantContext, CatalogKnowledgeSync sync) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<CatalogItem>> ListAsync(Guid tenantId, CatalogKind? kind, bool includeInactive, Guid? branchId, CancellationToken cancellationToken)
    {
        await sync.EnsureSeededAsync(tenantId, cancellationToken);
        var query = Db.CatalogItems.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (kind is not null)
        {
            query = query.Where(c => c.Kind == kind);
        }

        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        if (branchId is { } b)
        {
            query = query.Where(c => !Db.CatalogItemBranches.Any(x => x.CatalogItemId == c.Id)
                                     || Db.CatalogItemBranches.Any(x => x.CatalogItemId == c.Id && x.BranchId == b));
        }

        return await query.OrderBy(c => c.Kind).ThenBy(c => c.GroupName).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(cancellationToken);
    }

    public async Task<CatalogItemDetail> GetAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await RequireAsync(tenantId, itemId, cancellationToken);
        var branches = await Db.CatalogItemBranches.AsNoTracking().Where(x => x.TenantId == tenantId && x.CatalogItemId == itemId)
            .Select(x => x.BranchId).ToListAsync(cancellationToken);
        var contents = await Db.PackageContents.AsNoTracking().Where(p => p.TenantId == tenantId && p.PackageId == itemId)
            .ToListAsync(cancellationToken);
        return new CatalogItemDetail(item, branches, contents);
    }

    public async Task<CatalogItem> CreateAsync(Guid tenantId, CatalogItemInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        var code = NormalizeCode(input.Code);
        if (await Db.CatalogItems.AnyAsync(c => c.TenantId == tenantId && c.Code == code, cancellationToken))
        {
            throw new ConflictException($"Mã {code} đã có trong danh mục.", "duplicate_code");
        }

        var item = new CatalogItem
        {
            TenantId = tenantId,
            Kind = input.Kind,
            Code = code,
            Name = "",
            SortOrder = await Db.CatalogItems.CountAsync(c => c.TenantId == tenantId, cancellationToken),
        };
        Apply(item, input);
        Db.CatalogItems.Add(item);
        await sync.PushToKnowledgeAsync(tenantId, item, _caller.UserId, cancellationToken);
        Audit(tenantId, "catalog.created", $"{item.Id}:{code}");
        await Db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<CatalogItem> UpdateAsync(Guid tenantId, Guid itemId, CatalogItemInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var item = await RequireAsync(tenantId, itemId, cancellationToken);
        var code = NormalizeCode(input.Code);
        if (code != item.Code)
        {
            throw new InvalidInputException("Không đổi được mã của mục đã tạo (mã là khóa đồng bộ với kho kiến thức).");
        }

        if (input.Kind != item.Kind)
        {
            throw new InvalidInputException("Không đổi được loại (dịch vụ / gói / sản phẩm) của mục đã tạo.");
        }

        Apply(item, input);
        await sync.PushToKnowledgeAsync(tenantId, item, _caller.UserId, cancellationToken);
        Audit(tenantId, "catalog.updated", $"{item.Id}:{code}");
        await Db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task SetActiveAsync(Guid tenantId, Guid itemId, bool active, CancellationToken cancellationToken)
    {
        var item = await RequireAsync(tenantId, itemId, cancellationToken);
        item.IsActive = active;
        Audit(tenantId, active ? "catalog.activated" : "catalog.deactivated", $"{item.Id}:{item.Code}");
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Mục bán ở chi nhánh nào; danh sách rỗng = mọi chi nhánh.</summary>
    public async Task SetBranchesAsync(Guid tenantId, Guid itemId, IReadOnlyList<Guid> branchIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branchIds);
        await RequireAsync(tenantId, itemId, cancellationToken);
        var distinct = branchIds.Distinct().ToList();
        var known = await Db.Branches.Where(b => b.TenantId == tenantId && distinct.Contains(b.Id)).Select(b => b.Id).ToListAsync(cancellationToken);
        if (known.Count != distinct.Count)
        {
            throw new NotFoundException();
        }

        await Db.CatalogItemBranches.Where(x => x.TenantId == tenantId && x.CatalogItemId == itemId).ExecuteDeleteAsync(cancellationToken);
        foreach (var branchId in known)
        {
            Db.CatalogItemBranches.Add(new CatalogItemBranch { TenantId = tenantId, CatalogItemId = itemId, BranchId = branchId });
        }

        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Gói gồm dịch vụ nào, mấy buổi. Tổng số buổi của gói = tổng các dòng (cập nhật SessionCount).</summary>
    public async Task SetPackageContentsAsync(Guid tenantId, Guid packageId, IReadOnlyList<PackageContentInput> contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var package = await RequireAsync(tenantId, packageId, cancellationToken);
        if (package.Kind != CatalogKind.Package)
        {
            throw new InvalidInputException("Chỉ gói liệu trình mới có nội dung gói.");
        }

        if (contents.Any(c => c.SessionCount <= 0) || contents.Select(c => c.ServiceId).Distinct().Count() != contents.Count)
        {
            throw new InvalidInputException("Mỗi dịch vụ một dòng, số buổi phải lớn hơn 0.");
        }

        var serviceIds = contents.Select(c => c.ServiceId).ToList();
        var services = await Db.CatalogItems.Where(c => c.TenantId == tenantId && serviceIds.Contains(c.Id) && c.Kind == CatalogKind.Service)
            .Select(c => c.Id).ToListAsync(cancellationToken);
        if (services.Count != serviceIds.Count)
        {
            throw new NotFoundException();
        }

        await Db.PackageContents.Where(p => p.TenantId == tenantId && p.PackageId == packageId).ExecuteDeleteAsync(cancellationToken);
        foreach (var c in contents)
        {
            Db.PackageContents.Add(new PackageContent { TenantId = tenantId, PackageId = packageId, ServiceId = c.ServiceId, SessionCount = c.SessionCount });
        }

        if (contents.Count > 0)
        {
            package.SessionCount = contents.Sum(c => c.SessionCount);
            await sync.PushToKnowledgeAsync(tenantId, package, _caller.UserId, cancellationToken);
        }

        Audit(tenantId, "catalog.package_contents_updated", package.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<CatalogItem> RequireAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.CatalogItems.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == itemId, cancellationToken)
            ?? throw new NotFoundException();
    }

    private static void Apply(CatalogItem item, CatalogItemInput input)
    {
        if (input.Price is < 0 || input.CostPrice is < 0)
        {
            throw new InvalidInputException("Giá không được âm.");
        }

        item.Name = input.Name.Trim();
        item.GroupName = Trim(input.GroupName);
        item.Price = input.Price;
        item.PriceNote = Trim(input.PriceNote);
        item.BufferMinutes = Math.Max(0, input.BufferMinutes);
        item.Unit = Trim(input.Unit);
        switch (input.Kind)
        {
            case CatalogKind.Service:
                item.DurationMinutes = Positive(input.DurationMinutes);
                item.Unit ??= "lượt";
                item.SessionCount = null;
                item.ValidityDays = null;
                item.Sku = null;
                item.CostPrice = null;
                item.TrackInventory = false;
                break;
            case CatalogKind.Package:
                item.DurationMinutes = Positive(input.DurationMinutes);
                item.SessionCount = Positive(input.SessionCount);
                item.ValidityDays = Positive(input.ValidityDays);
                item.Unit ??= "gói";
                item.Sku = null;
                item.CostPrice = null;
                item.TrackInventory = false;
                break;
            default:
                item.DurationMinutes = null;
                item.BufferMinutes = 0;
                item.SessionCount = null;
                item.ValidityDays = null;
                item.Sku = Trim(input.Sku);
                item.CostPrice = input.CostPrice;
                item.TrackInventory = input.TrackInventory;
                break;
        }
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string NormalizeCode(string raw) =>
        KnowledgeTemplate.NormalizeCode(raw) ?? throw new InvalidInputException("Mã chỉ gồm chữ in hoa không dấu, số, gạch ngang (2–50 ký tự).");

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });
}
