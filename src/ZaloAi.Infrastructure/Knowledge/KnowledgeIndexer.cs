using System.Text.Json;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Tạo vector cho các đoạn chưa có (theo lô), lưu sau mỗi lô để lỗi giữa chừng không mất phần đã làm.</summary>
public sealed class KnowledgeIndexer(AppDbContext db, ChunkRepository chunks, IEmbeddingProvider embeddings)
{
    private const int BatchSize = 50;

    // Chặn vòng lặp vô tận nếu có lỗi logic (mỗi vòng phải giảm số đoạn chờ).
    private const int MaxBatches = 1000;

    /// <returns>Số đoạn đã tạo vector.</returns>
    public async Task<int> IndexPendingAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var round = 0; round < MaxBatches; round++)
        {
            var pending = await chunks.ListPendingEmbeddingAsync(tenantId, BatchSize, cancellationToken);
            if (pending.Count == 0)
            {
                break;
            }

            var vectors = await embeddings.EmbedDocumentsAsync(
                pending.Select(c => new EmbeddingDocument(TitleOf(c), c.Content)).ToList(),
                cancellationToken);

            for (var i = 0; i < pending.Count; i++)
            {
                pending[i].Embedding = vectors[i];
                pending[i].EmbeddingModel = embeddings.ModelName;
            }

            await db.SaveChangesAsync(cancellationToken);
            total += pending.Count;
        }

        return total;
    }

    private static string? TitleOf(Chunk chunk)
    {
        using var meta = JsonDocument.Parse(chunk.MetaJson);
        return meta.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }
}
