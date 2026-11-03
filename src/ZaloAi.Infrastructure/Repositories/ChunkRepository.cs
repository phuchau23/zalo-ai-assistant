using Microsoft.EntityFrameworkCore;
using Pgvector;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Kết quả tìm kiếm: khoảng cách cosine càng nhỏ càng giống (0 = giống hệt).</summary>
public sealed record ChunkSearchResult(
    Guid ChunkId,
    Guid? KnowledgeItemId,
    Guid? DocumentId,
    string Content,
    string MetaJson,
    double Distance,
    bool MedicallyReviewed = false);

public sealed class ChunkRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public void AddRange(Guid tenantId, IEnumerable<Chunk> chunks)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            chunk.TenantId = tenantId;
            if (chunk.Id == Guid.Empty)
            {
                chunk.Id = Guid.CreateVersion7();
            }

            Db.Chunks.Add(chunk);
        }
    }

    /// <summary>Xóa ngay (không qua SaveChanges) các đoạn của một mục — dùng khi nội dung mục đổi.</summary>
    public Task<int> DeleteForItemAsync(Guid tenantId, Guid knowledgeItemId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Chunks
            .Where(c => c.TenantId == tenantId && c.KnowledgeItemId == knowledgeItemId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> DeleteForDocumentAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Chunks
            .Where(c => c.TenantId == tenantId && c.DocumentId == documentId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Các đoạn chưa có embedding (cần đánh chỉ mục), được track để gán embedding rồi SaveChanges.</summary>
    public async Task<IReadOnlyList<Chunk>> ListPendingEmbeddingAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Chunks
            .Where(c => c.TenantId == tenantId && c.Embedding == null)
            .OrderBy(c => c.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Tìm k đoạn gần nhất với vector câu hỏi, CHỈ trong tenant này (CLAUDE.md mục 8: lọc tenant_id trước khi sắp xếp).
    /// SQL thô vì cần toán tử pgvector; điều kiện tenant_id có trong câu SQL.
    /// iterative_scan: nếu chỉ mục HNSW trả về nhiều đoạn của tenant khác, pgvector quét tiếp thay vì trả thiếu kết quả.
    /// </summary>
    public async Task<IReadOnlyList<ChunkSearchResult>> SearchAsync(
        Guid tenantId,
        float[] queryEmbedding,
        int k,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        var limit = Math.Clamp(k, 1, 50);
        var vector = new Vector(queryEmbedding);

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        await Db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = relaxed_order", cancellationToken);

        var rows = await Db.Database.SqlQuery<ChunkSearchRow>($"""
            SELECT c.id, c.knowledge_item_id, c.document_id, c.content, c.meta_json, c.distance,
                   COALESCE(i.medically_reviewed, d.medically_reviewed, FALSE) AS medically_reviewed
            FROM (
                SELECT id, knowledge_item_id, document_id, content, meta::text AS meta_json,
                       (embedding <=> {vector}) AS distance
                FROM chunks
                WHERE tenant_id = {tenantId} AND embedding IS NOT NULL
                ORDER BY embedding <=> {vector}
                LIMIT {limit}
            ) c
            LEFT JOIN knowledge_items i ON i.id = c.knowledge_item_id AND i.tenant_id = {tenantId}
            LEFT JOIN documents d ON d.id = c.document_id AND d.tenant_id = {tenantId}
            """).ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // relaxed_order có thể trả lệch thứ tự chút ít: sắp lại theo khoảng cách.
        return rows
            .OrderBy(r => r.Distance)
            .Select(r => new ChunkSearchResult(r.Id, r.KnowledgeItemId, r.DocumentId, r.Content, r.MetaJson, r.Distance, r.MedicallyReviewed))
            .ToList();
    }

    private sealed class ChunkSearchRow
    {
        public Guid Id { get; set; }

        public Guid? KnowledgeItemId { get; set; }

        public Guid? DocumentId { get; set; }

        public string Content { get; set; } = "";

        public string MetaJson { get; set; } = "";

        public double Distance { get; set; }

        public bool MedicallyReviewed { get; set; }
    }
}
