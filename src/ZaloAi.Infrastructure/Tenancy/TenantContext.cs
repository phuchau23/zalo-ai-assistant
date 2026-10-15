using ZaloAi.Core.Tenancy;

namespace ZaloAi.Infrastructure.Tenancy;

/// <summary>
/// Scoped: mỗi request/job một instance. Chỉ middleware auth (API) và đầu job (Worker) được gọi <see cref="Set"/>.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private bool _isSet;

    public Guid? TenantId { get; private set; }

    public Guid? UserId { get; private set; }

    public TenantRole? Role { get; private set; }

    public bool IsSuperAdmin { get; private set; }

    public IReadOnlySet<string> Permissions { get; private set; } = None;

    public Guid? BranchId { get; private set; }

    public void Set(Guid? tenantId, Guid? userId, TenantRole? role, bool isSuperAdmin) =>
        Set(tenantId, userId, role, isSuperAdmin, permissions: null, branchId: null);

    /// <param name="permissions">Quyền thêm của nhân viên (bỏ qua với chủ).</param>
    /// <param name="branchId">Chi nhánh nhân viên được xem; null = mọi chi nhánh. Chủ hoặc có quyền xem mọi chi nhánh → null.</param>
    public void Set(Guid? tenantId, Guid? userId, TenantRole? role, bool isSuperAdmin, IEnumerable<string>? permissions, Guid? branchId)
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
        Permissions = role == TenantRole.Staff && permissions is not null
            ? new HashSet<string>(permissions.Where(TenantPermissions.IsKnown), StringComparer.Ordinal)
            : None;
        BranchId = role == TenantRole.Staff && !Permissions.Contains(TenantPermissions.ViewAllBranches) ? branchId : null;
        _isSet = true;
    }

    public bool Can(string permission) => Role != TenantRole.Staff || Permissions.Contains(permission);

    public Guid RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException("Chưa xác định tenant cho request/job hiện tại.");
}
