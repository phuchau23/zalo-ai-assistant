using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

public sealed record ResourceInput(Guid BranchId, string Name, ResourceKind Kind);

/// <summary>Giường / phòng / máy (CRM C2). Số tài nguyên đang hoạt động của chi nhánh = sức chứa lịch cùng lúc.</summary>
public sealed class ResourceService(AppDbContext db, ITenantContext tenantContext, BranchService branches) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<Resource>> ListAsync(Guid tenantId, Guid? branchId, bool includeInactive, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Resources.AsNoTracking().Where(r => r.TenantId == tenantId);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(r => r.BranchId == b);
        }

        if (!includeInactive)
        {
            query = query.Where(r => r.IsActive);
        }

        return await query.OrderBy(r => r.BranchId).ThenBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(cancellationToken);
    }

    public async Task<Resource> CreateAsync(Guid tenantId, ResourceInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        await branches.RequireAsync(tenantId, input.BranchId, cancellationToken);
        var resource = new Resource
        {
            TenantId = tenantId,
            BranchId = input.BranchId,
            Name = input.Name.Trim(),
            Kind = input.Kind,
            SortOrder = await Db.Resources.CountAsync(r => r.TenantId == tenantId && r.BranchId == input.BranchId, cancellationToken),
        };
        Db.Resources.Add(resource);
        Audit(tenantId, "resource.created", resource.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    public async Task<Resource> UpdateAsync(Guid tenantId, Guid resourceId, ResourceInput input, bool isActive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var resource = await RequireAsync(tenantId, resourceId, cancellationToken);
        await branches.RequireAsync(tenantId, input.BranchId, cancellationToken);
        resource.BranchId = input.BranchId;
        resource.Name = input.Name.Trim();
        resource.Kind = input.Kind;
        resource.IsActive = isActive;
        Audit(tenantId, "resource.updated", resource.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return resource;
    }

    private async Task<Resource> RequireAsync(Guid tenantId, Guid resourceId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var resource = await Db.Resources.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == resourceId, cancellationToken) ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && resource.BranchId != only)
        {
            throw new NotFoundException();
        }

        return resource;
    }

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });
}
