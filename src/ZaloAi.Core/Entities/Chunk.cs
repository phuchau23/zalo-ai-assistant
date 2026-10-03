namespace ZaloAi.Core.Entities;

/// <summary>
/// Một đoạn nội dung đã/đang được đánh chỉ mục để bot tìm kiếm. Đến từ đúng một nguồn:
/// một mục kiến thức (<see cref="KnowledgeItemId"/>) hoặc một tài liệu tự do (<see cref="DocumentId"/>).
/// Xóa nguồn → xóa luôn các đoạn của nó.
/// </summary>
public sealed class Chunk : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? KnowledgeItemId { get; set; }

    public Guid? DocumentId { get; set; }

    /// <summary>Thứ tự đoạn trong nguồn (tài liệu dài chia nhiều đoạn).</summary>
    public int Ordinal { get; set; }

    public required string Content { get; set; }

    /// <summary>Thông tin phụ (jsonb): tiêu đề mục, mã, loại... để hiển thị nguồn trả lời.</summary>
    public required string MetaJson { get; set; }

    /// <summary>Vector embedding; null cho tới khi đánh chỉ mục xong. Số chiều cố định theo cột (768).</summary>
    public float[]? Embedding { get; set; }

    /// <summary>Model đã tạo <see cref="Embedding"/>; đổi model = phải đánh chỉ mục lại.</summary>
    public string? EmbeddingModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
