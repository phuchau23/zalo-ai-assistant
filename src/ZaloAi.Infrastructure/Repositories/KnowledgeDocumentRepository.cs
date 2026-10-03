using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed class KnowledgeDocumentRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public async Task<IReadOnlyList<KnowledgeDocument>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId)
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Được track. Không thuộc tenant → null (API trả 404).</summary>
    public Task<KnowledgeDocument?> GetAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Documents
            .Where(d => d.TenantId == tenantId && d.Id == documentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<KnowledgeDocument?> FindByFileNameAsync(Guid tenantId, string fileName, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Documents
            .Where(d => d.TenantId == tenantId && d.FileName == fileName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public KnowledgeDocument Add(Guid tenantId, KnowledgeDocument document)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(document);
        document.TenantId = tenantId;
        if (document.Id == Guid.Empty)
        {
            document.Id = Guid.CreateVersion7();
        }

        Db.Documents.Add(document);
        return document;
    }

    /// <summary>Xóa bản ghi; các đoạn bị xóa theo (cascade). File gốc do tầng lưu trữ xóa riêng.</summary>
    public void Remove(Guid tenantId, KnowledgeDocument document)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(document);
        if (document.TenantId != tenantId)
        {
            throw new TenantIsolationException("Tài liệu không thuộc tenant hiện tại.");
        }

        Db.Documents.Remove(document);
    }
}
