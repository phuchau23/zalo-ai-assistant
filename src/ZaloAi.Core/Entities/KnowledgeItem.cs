namespace ZaloAi.Core.Entities;

/// <summary>Năm loại mục của mẫu dữ liệu chuẩn (docs/KNOWLEDGE-FORMAT.md).</summary>
public enum KnowledgeKind
{
    Info = 0,
    Service = 1,
    Package = 2,
    Faq = 3,
    Policy = 4,
}

/// <summary>
/// Một mục kiến thức có cấu trúc (một dịch vụ, một câu hỏi thường gặp...). Mã (<see cref="Code"/>) ổn định,
/// không trùng trong một tenant, là khóa để so sánh khi nhập bản mới.
/// Nội dung là thông tin công khai của doanh nghiệp (bảng giá, dịch vụ) nên không mã hóa.
/// </summary>
public sealed class KnowledgeItem : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public KnowledgeKind Kind { get; set; }

    /// <summary>Chữ in hoa không dấu, số, gạch ngang, 2–50 ký tự.</summary>
    public required string Code { get; set; }

    /// <summary>Các trường của mục theo loại, dạng JSON (jsonb). Tên trường như định dạng JSON của mẫu.</summary>
    public required string DataJson { get; set; }

    /// <summary>Nội dung ghép thành văn bản để đánh chỉ mục và hiển thị.</summary>
    public required string SearchText { get; set; }

    /// <summary>SHA-256 (hex) của nội dung đã chuẩn hóa: so sánh nhanh có thay đổi không.</summary>
    public required string ContentHash { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
