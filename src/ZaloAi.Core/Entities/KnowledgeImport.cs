namespace ZaloAi.Core.Entities;

public enum KnowledgeImportFormat
{
    Xlsx = 0,
    Json = 1,
}

public enum KnowledgeImportStatus
{
    /// <summary>Đã so sánh, chờ người dùng chọn mục và áp dụng.</summary>
    Preview = 0,
    Applied = 1,
    Discarded = 2,
}

/// <summary>
/// Một lần nhập file mẫu: bản so sánh (xem trước) và, sau khi áp dụng, những mục đã được chọn.
/// Đồng thời là lịch sử nhập (ai, lúc nào, đổi gì).
/// </summary>
public sealed class KnowledgeImport : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string FileName { get; set; }

    public KnowledgeImportFormat SourceFormat { get; set; }

    public KnowledgeImportStatus Status { get; set; } = KnowledgeImportStatus.Preview;

    /// <summary>Bản so sánh từng mục (jsonb). Cấu trúc do code so sánh (bước 4) quyết định.</summary>
    public required string DiffJson { get; set; }

    /// <summary>Các mục được chọn khi áp dụng (jsonb), null khi chưa áp dụng.</summary>
    public string? SelectionJson { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? AppliedBy { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }
}
