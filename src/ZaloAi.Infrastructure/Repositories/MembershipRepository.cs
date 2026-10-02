using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed class MembershipRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public Task<Membership?> GetAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Memberships
            .Where(m => m.TenantId == tenantId && m.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Membership>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Memberships
            .AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Membership Add(Guid tenantId, Guid userId, TenantRole role)
    {
        EnsureTenant(tenantId);
        var membership = new Membership { TenantId = tenantId, UserId = userId, Role = role };
        Db.Memberships.Add(membership);
        return membership;
    }
}
