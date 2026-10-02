namespace ZaloAi.Core.Entities;

/// <summary>
/// Ai làm gì, lúc nào (xem/xuất dữ liệu, đổi cài đặt, đăng nhập...). Chỉ thêm, không sửa/xóa.
/// Cố ý KHÔNG implement <see cref="ITenantOwned"/>: <see cref="TenantId"/> null với hành động cấp hệ thống
/// (super admin, đăng nhập thất bại). Repository lọc tenant thủ công.
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>Dạng "đối_tượng.hành_động", ví dụ "tenant.settings_updated", "contacts.exported".</summary>
    public required string Action { get; set; }

    /// <summary>Id hoặc mô tả ngắn của đối tượng bị tác động. Không chứa PII.</summary>
    public string? Target { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
