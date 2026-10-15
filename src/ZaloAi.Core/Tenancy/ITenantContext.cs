namespace ZaloAi.Core.Tenancy;

/// <summary>
/// Tenant của request/job hiện tại. API: lấy từ cookie session. Worker: lấy từ tham số job.
/// Không bao giờ lấy từ body/query/route hay từ input của khách cuối.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    Guid? UserId { get; }

    TenantRole? Role { get; }

    bool IsSuperAdmin { get; }

    /// <summary>Quyền thêm của nhân viên (<see cref="TenantPermissions"/>). Chủ DN: rỗng nhưng <see cref="Can"/> luôn true.</summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>Chi nhánh người này được xem khi KHÔNG có quyền xem mọi chi nhánh; null = mọi chi nhánh (chủ, job hệ thống).</summary>
    Guid? BranchId { get; }

    /// <summary>Chủ DN hoặc job hệ thống (không có vai trò) → true; nhân viên → có trong <see cref="Permissions"/>.</summary>
    bool Can(string permission);

    /// <exception cref="InvalidOperationException">Chưa có tenant (lỗi lập trình, không phải lỗi người dùng).</exception>
    Guid RequireTenantId();
}
