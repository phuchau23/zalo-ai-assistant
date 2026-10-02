using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed class TenantRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Entity được track: sửa thuộc tính rồi gọi <see cref="TenantScopedRepository.SaveChangesAsync"/>.</summary>
    public Task<Tenant?> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Tenants.Where(t => t.Id == tenantId).FirstOrDefaultAsync(cancellationToken);
    }
}
