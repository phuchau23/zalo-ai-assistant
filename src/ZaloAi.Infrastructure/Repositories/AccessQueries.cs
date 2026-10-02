using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed record UserAccess(Guid UserId, string Email, string Name, bool IsSuperAdmin);

public sealed record TenantAccess(Guid TenantId, string TenantName, TenantRole Role, TenantStatus Status);

/// <summary>
/// Truy vấn phục vụ xác thực: user đang đăng nhập thuộc những tenant nào, vai trò gì.
/// Dùng IgnoreQueryFilters() CÓ CHỦ ĐÍCH: lúc này tenant context chưa được set (chính các truy vấn này quyết định tenant).
/// Mọi truy vấn đều lọc theo user_id của người đang đăng nhập (lấy từ cookie đã xác thực hoặc mật khẩu đã kiểm),
/// không bao giờ theo input khác của client, và chỉ trả tên + vai trò, không trả dữ liệu nghiệp vụ của tenant.
/// </summary>
public sealed class AccessQueries(AppDbContext db)
{
    public Task<UserAccess?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserAccess(u.Id, u.Email, u.Name, u.IsSuperAdmin))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Mọi tenant của user, cũ nhất trước (tenant mặc định khi đăng nhập = tenant đầu tiên còn hoạt động).</summary>
    public async Task<IReadOnlyList<TenantAccess>> ListTenantsForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await Query(userId, tenantId: null).ToListAsync(cancellationToken);

    public Task<TenantAccess?> GetTenantAccessAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Query(userId, tenantId).FirstOrDefaultAsync(cancellationToken);

    private IQueryable<TenantAccess> Query(Guid userId, Guid? tenantId) =>
        from m in db.Memberships.IgnoreQueryFilters()
        join t in db.Tenants.IgnoreQueryFilters() on m.TenantId equals t.Id
        where m.UserId == userId && (tenantId == null || m.TenantId == tenantId)
        orderby m.CreatedAt
        select new TenantAccess(t.Id, t.Name, m.Role, t.Status);
}
