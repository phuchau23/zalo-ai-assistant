using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed class KnowledgeItemRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public async Task<IReadOnlyList<KnowledgeItem>> ListAsync(Guid tenantId, KnowledgeKind? kind, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.KnowledgeItems.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (kind is not null)
        {
            query = query.Where(i => i.Kind == kind);
        }

        return await query.OrderBy(i => i.Kind).ThenBy(i => i.Code).ToListAsync(cancellationToken);
    }

    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<KnowledgeItem?> GetAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.KnowledgeItems.Where(i => i.TenantId == tenantId && i.Id == itemId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Các mục theo mã, được track để sửa/xóa rồi gọi SaveChangesAsync.</summary>
    public async Task<IReadOnlyList<KnowledgeItem>> GetByCodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.KnowledgeItems
            .Where(i => i.TenantId == tenantId && codes.Contains(i.Code))
            .ToListAsync(cancellationToken);
    }

    public KnowledgeItem Add(Guid tenantId, KnowledgeItem item)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(item);
        item.TenantId = tenantId;
        if (item.Id == Guid.Empty)
        {
            item.Id = Guid.CreateVersion7();
        }

        Db.KnowledgeItems.Add(item);
        return item;
    }

    /// <summary>Xóa mục; các đoạn (chunks) của nó bị xóa theo (cascade).</summary>
    public void Remove(Guid tenantId, KnowledgeItem item)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(item);
        if (item.TenantId != tenantId)
        {
            throw new TenantIsolationException("Mục kiến thức không thuộc tenant hiện tại.");
        }

        Db.KnowledgeItems.Remove(item);
    }
}
