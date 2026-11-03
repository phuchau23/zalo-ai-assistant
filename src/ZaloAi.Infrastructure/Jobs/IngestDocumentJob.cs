using System.Text.Json;
using Hangfire;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Storage;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Đọc tài liệu tự do → chia đoạn → tạo vector → trạng thái "ready". Chạy lại vô hại (xóa đoạn cũ của tài liệu trước khi tạo lại).
/// File hỏng / PDF ảnh → "failed" kèm lý do, không thử lại. Lỗi gọi AI → ném ra để Hangfire thử lại.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class IngestDocumentJob(
    TenantContext tenantContext,
    AppDbContext db,
    KnowledgeDocumentRepository documents,
    ChunkRepository chunks,
    IFileStorage storage,
    KnowledgeIndexer indexer)
{
    public const int MaxChunks = 2000;

    public async Task RunAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);

        var document = await documents.GetAsync(tenantId, documentId, cancellationToken);
        if (document is null)
        {
            return; // đã bị xóa trong lúc chờ
        }

        document.Status = DocumentStatus.Processing;
        document.Error = null;
        await db.SaveChangesAsync(cancellationToken);

        string text;
        try
        {
            await using var file = await storage.OpenReadAsync(document.StorageKey, cancellationToken);
            text = DocumentText.Extract(document.FileName, file);
        }
        catch (DocumentReadException ex)
        {
            await FailAsync(document, ex.Message, cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            await FailAsync(document, "Không tìm thấy file gốc. Hãy nạp lại tài liệu.", cancellationToken);
            return;
        }

        var pieces = TextChunker.Split(text);
        if (pieces.Count > MaxChunks)
        {
            await FailAsync(document, $"Tài liệu quá dài (hơn {MaxChunks} đoạn). Hãy chia thành nhiều file nhỏ hơn.", cancellationToken);
            return;
        }

        await chunks.DeleteForDocumentAsync(tenantId, document.Id, cancellationToken);
        chunks.AddRange(tenantId, pieces.Select(p => new Chunk
        {
            DocumentId = document.Id,
            Ordinal = p.Ordinal,
            Content = p.Content,
            MetaJson = JsonSerializer.Serialize(new
            {
                source = "document",
                documentId = document.Id,
                title = p.Section is null ? document.Title : $"{document.Title} — {p.Section}",
                section = p.Section,
                ordinal = p.Ordinal,
            }),
        }));
        document.ChunkCount = pieces.Count;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await indexer.IndexPendingAsync(tenantId, cancellationToken);
        }
        catch (AiProviderException)
        {
            document.Error = "Đang chờ dịch vụ AI tạo chỉ mục, hệ thống sẽ tự thử lại.";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        document.Status = DocumentStatus.Ready;
        document.Error = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(KnowledgeDocument document, string reason, CancellationToken cancellationToken)
    {
        document.Status = DocumentStatus.Failed;
        document.Error = reason;
        document.ChunkCount = 0;
        await db.SaveChangesAsync(cancellationToken);
    }
}
