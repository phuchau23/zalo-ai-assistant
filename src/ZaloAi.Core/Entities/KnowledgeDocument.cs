namespace ZaloAi.Core.Entities;

public enum DocumentStatus
{
    Pending = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
}

/// <summary>
/// Tài liệu tự do (PDF, Word, Excel không theo mẫu, TXT, MD) làm "tài liệu tham khảo".
/// Tên file không trùng trong một tenant: nạp lại cùng tên = thay thế (sau khi người dùng xác nhận).
/// </summary>
public sealed class KnowledgeDocument : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Đường dẫn tương đối trong kho lưu file (local khi dev, object storage khi production).</summary>
    public required string StorageKey { get; set; }

    /// <summary>SHA-256 (hex) của file gốc.</summary>
    public required string Sha256 { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>Lý do lỗi khi đọc/đánh chỉ mục (an toàn để hiện cho người dùng).</summary>
    public string? Error { get; set; }

    public int ChunkCount { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
