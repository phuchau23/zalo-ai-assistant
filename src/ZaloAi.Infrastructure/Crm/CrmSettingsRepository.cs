using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

/// <summary>Cài đặt CRM theo tenant (tạo lười như <see cref="HandoffSettings"/>).</summary>
public sealed class CrmSettingsRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Được track; tạo dòng mới nếu chưa có.</summary>
    public async Task<CrmSettings> GetOrCreateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var settings = await Db.CrmSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (settings is null)
        {
            settings = new CrmSettings { TenantId = tenantId };
            Db.CrmSettings.Add(settings);
        }

        return settings;
    }

    public async Task<CrmSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.CrmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken)
            ?? new CrmSettings { TenantId = tenantId };
    }
}
