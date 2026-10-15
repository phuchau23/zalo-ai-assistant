using ZaloAi.Core.Tenancy;

namespace ZaloAi.Core.Entities;

/// <summary>User thuộc tenant nào, với vai trò gì. Khóa chính (user_id, tenant_id).</summary>
public sealed class Membership : ITenantOwned
{
    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public TenantRole Role { get; set; }

    /// <summary>Quyền thêm cho nhân viên (<see cref="TenantPermissions"/>); bỏ qua với chủ DN.</summary>
    public string[] Permissions { get; set; } = [];

    /// <summary>Chi nhánh làm việc; null = mọi chi nhánh. Nhân viên không có quyền xem mọi chi nhánh chỉ thấy chi nhánh này.</summary>
    public Guid? BranchId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User? User { get; set; }
}
