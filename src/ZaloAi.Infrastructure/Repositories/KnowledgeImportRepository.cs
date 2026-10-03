using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed class KnowledgeImportRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public KnowledgeImport Add(Guid tenantId, KnowledgeImport import)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(import);
        import.TenantId = tenantId;
        if (import.Id == Guid.Empty)
        {
            import.Id = Guid.CreateVersion7();
        }

        Db.KnowledgeImports.Add(import);
        return import;
    }

    /// <summary>Được track. Không thuộc tenant → null (API trả 404).</summary>
    public Task<KnowledgeImport?> GetAsync(Guid tenantId, Guid importId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.KnowledgeImports
            .Where(i => i.TenantId == tenantId && i.Id == importId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Lịch sử nhập, mới nhất trước.</summary>
    public async Task<IReadOnlyList<KnowledgeImport>> ListAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.KnowledgeImports
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(i => new KnowledgeImport
            {
                Id = i.Id,
                TenantId = i.TenantId,
                FileName = i.FileName,
                SourceFormat = i.SourceFormat,
                Status = i.Status,
                DiffJson = i.DiffJson, // cần phần tóm tắt cho lịch sử; giới hạn số dòng ở Take()
                CreatedBy = i.CreatedBy,
                CreatedAt = i.CreatedAt,
                AppliedBy = i.AppliedBy,
                AppliedAt = i.AppliedAt,
            })
            .ToListAsync(cancellationToken);
    }
}
