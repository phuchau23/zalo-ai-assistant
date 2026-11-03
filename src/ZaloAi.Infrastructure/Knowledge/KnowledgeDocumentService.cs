using System.Security.Cryptography;
using System.Text;
using Hangfire;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Storage;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Nạp / xóa tài liệu tự do. Đọc nội dung + đánh chỉ mục chạy nền (<see cref="IngestDocumentJob"/>).</summary>
public sealed class KnowledgeDocumentService(
    AppDbContext db,
    KnowledgeDocumentRepository documents,
    ChunkRepository chunks,
    AuditLogRepository audit,
    IFileStorage storage,
    IBackgroundJobClient jobs)
{
    /// <summary>Giới hạn mặc định của dự án (CLAUDE.md mục 8).</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;

    public async Task<KnowledgeDocument> UploadAsync(
        Guid tenantId,
        Guid? userId,
        string fileName,
        string? contentType,
        Stream content,
        bool replace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safeName = SafeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (!DocumentText.SupportedExtensions.Contains(extension))
        {
            throw new InvalidInputException("Chỉ nhận PDF, Word (.docx), Excel (.xlsx), TXT, MD. File Word cũ (.doc) hãy lưu lại thành .docx.");
        }

        // Đọc vào bộ nhớ (≤ 20MB) để kiểm tra nội dung thật và tính SHA-256 trước khi lưu.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
        {
            throw new InvalidInputException("File rỗng.");
        }

        if (buffer.Length > MaxFileBytes)
        {
            throw new InvalidInputException($"File quá lớn (tối đa {MaxFileBytes / 1024 / 1024} MB).");
        }

        if (!LooksLike(extension, buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 4096))))
        {
            throw new InvalidInputException("Nội dung file không đúng với đuôi file. Hãy mở và lưu lại bằng đúng định dạng.");
        }

        var existing = await documents.FindByFileNameAsync(tenantId, safeName, cancellationToken);
        if (existing is not null && !replace)
        {
            throw new ConflictException($"Đã có tài liệu tên \"{safeName}\". Chọn thay thế nếu muốn cập nhật bản mới.", "document_exists");
        }

        var document = existing ?? documents.Add(tenantId, new KnowledgeDocument
        {
            Title = Path.GetFileNameWithoutExtension(safeName),
            FileName = safeName,
            ContentType = "",
            StorageKey = "",
            Sha256 = "",
            CreatedBy = userId,
        });

        if (existing is not null)
        {
            await chunks.DeleteForDocumentAsync(tenantId, existing.Id, cancellationToken);
            await storage.DeleteAsync(existing.StorageKey, cancellationToken);
        }

        document.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType[..Math.Min(contentType.Length, 150)];
        document.SizeBytes = buffer.Length;
        document.Sha256 = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
        document.StorageKey = $"{tenantId:N}/{document.Id:N}/{StorageName(safeName)}";
        document.Status = DocumentStatus.Pending;
        document.Error = null;
        document.ChunkCount = 0;
        document.MedicallyReviewed = false; // file mới → phải duyệt chuyên môn lại

        buffer.Position = 0;
        await storage.SaveAsync(document.StorageKey, buffer, cancellationToken);

        audit.Add(tenantId, userId, existing is null ? "knowledge.document_uploaded" : "knowledge.document_replaced", document.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);

        var documentId = document.Id;
        jobs.Enqueue<IngestDocumentJob>(job => job.RunAsync(tenantId, documentId, CancellationToken.None));
        return document;
    }

    public async Task DeleteAsync(Guid tenantId, Guid? userId, Guid documentId, CancellationToken cancellationToken)
    {
        var document = await documents.GetAsync(tenantId, documentId, cancellationToken) ?? throw new NotFoundException();
        documents.Remove(tenantId, document); // chunks bị xóa theo (cascade)
        audit.Add(tenantId, userId, "knowledge.document_deleted", document.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(document.StorageKey, cancellationToken);
    }

    /// <summary>Tên hiển thị: bỏ đường dẫn, ký tự điều khiển, ký tự cấm trên Windows; giới hạn độ dài.</summary>
    public static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        var cleaned = new string(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim().Trim('.');
        if (cleaned.Length == 0)
        {
            throw new InvalidInputException("Tên file không hợp lệ.");
        }

        var extension = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        return stem.Length > 200 ? stem[..200] + extension : cleaned;
    }

    /// <summary>Tên trên ổ đĩa: chỉ chữ số/ASCII an toàn (tên tiếng Việt vẫn giữ ở cột FileName).</summary>
    private static string StorageName(string safeName)
    {
        var ascii = new StringBuilder();
        foreach (var ch in safeName.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')
            {
                ascii.Append(ch);
            }
            else if (ch == ' ')
            {
                ascii.Append('-');
            }
        }

        return ascii.Length > 0 ? ascii.ToString() : "file";
    }

    /// <summary>Kiểm tra sơ bộ nội dung khớp đuôi file (chặn đổi đuôi .exe → .pdf...).</summary>
    private static bool LooksLike(string extension, ReadOnlySpan<byte> head) => extension switch
    {
        ".pdf" => head.StartsWith("%PDF"u8),
        ".docx" or ".xlsx" => head.StartsWith("PK"u8),
        _ => !head.Contains((byte)0),
    };
}
