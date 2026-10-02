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

    /// <exception cref="InvalidOperationException">Chưa có tenant (lỗi lập trình, không phải lỗi người dùng).</exception>
    Guid RequireTenantId();
}
