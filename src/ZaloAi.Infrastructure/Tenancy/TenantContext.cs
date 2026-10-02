using ZaloAi.Core.Tenancy;

namespace ZaloAi.Infrastructure.Tenancy;

/// <summary>
/// Scoped: mỗi request/job một instance. Chỉ middleware auth (API) và đầu job (Worker) được gọi <see cref="Set"/>.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private bool _isSet;

    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public TenantRole? Role { get; private set; }

    public bool IsSuperAdmin { get; private set; }

    public void Set(Guid? tenantId, Guid? userId, TenantRole? role, bool isSuperAdmin)
    {
        // Đổi tenant giữa chừng một scope là dấu hiệu lỗi nghiêm trọng.
        if (_isSet)
        {
            throw new InvalidOperationException("TenantContext đã được set trong scope này.");
        }

        TenantId = tenantId;
        UserId = userId;
        Role = role;
        IsSuperAdmin = isSuperAdmin;
        _isSet = true;
    }

    public Guid RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException("Chưa xác định tenant cho request/job hiện tại.");
}
