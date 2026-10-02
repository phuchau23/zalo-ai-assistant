using ZaloAi.Core.Tenancy;

namespace ZaloAi.Core.Entities;

/// <summary>User thuộc tenant nào, với vai trò gì. Khóa chính (user_id, tenant_id).</summary>
public sealed class Membership : ITenantOwned
{
    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public TenantRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public User? User { get; set; }
}
