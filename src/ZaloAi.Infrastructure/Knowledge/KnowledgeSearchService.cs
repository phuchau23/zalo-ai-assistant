using System.Text.Json;
using ZaloAi.Core.Ai;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Câu hỏi → vector → k đoạn gần nhất trong kho của tenant (ChunkRepository lọc tenant_id trước khi xếp).</summary>
public sealed class KnowledgeSearchService(IEmbeddingProvider embeddings, ChunkRepository chunks) : IKnowledgeSearch
{
    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var vector = await embeddings.EmbedQueryAsync(query.Trim(), cancellationToken);
        var hits = await chunks.SearchAsync(tenantId, vector, limit, cancellationToken);

        return hits.Select(h =>
        {
            using var meta = JsonDocument.Parse(h.MetaJson);
            string? Read(string name) => meta.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new KnowledgeHit(
                h.ChunkId,
                h.KnowledgeItemId is null ? "document" : "item",
                h.KnowledgeItemId ?? h.DocumentId ?? Guid.Empty,
                Read("code"),
                Read("title"),
                h.Content,
                Math.Round(Math.Clamp(1 - h.Distance, 0, 1), 4),
                h.MedicallyReviewed);
        }).ToList();
    }
}
