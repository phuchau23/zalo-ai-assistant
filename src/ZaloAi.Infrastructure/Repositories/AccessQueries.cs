using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Security;

namespace ZaloAi.Infrastructure.Repositories;

/// <param name="PasswordStamp">Dấu vân tay mật khẩu hiện tại (<see cref="Passwords.Stamp"/>) — so với cookie để đăng xuất phiên cũ khi đổi mật khẩu.</param>
public sealed record UserAccess(Guid UserId, string Email, string Name, bool IsSuperAdmin, string PasswordStamp = "");

public sealed record TenantAccess(Guid TenantId, string TenantName, TenantRole Role, TenantStatus Status, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Truy vấn phục vụ xác thực: user đang đăng nhập thuộc những tenant nào, vai trò gì.
/// Dùng IgnoreQueryFilters() CÓ CHỦ ĐÍCH: lúc này tenant context chưa được set (chính các truy vấn này quyết định tenant).
/// Mọi truy vấn đều lọc theo user_id của người đang đăng nhập (lấy từ cookie đã xác thực hoặc mật khẩu đã kiểm),
/// không bao giờ theo input khác của client, và chỉ trả tên + vai trò, không trả dữ liệu nghiệp vụ của tenant.
/// </summary>
public sealed class AccessQueries(AppDbContext db)
{
    public async Task<UserAccess?> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.Id, x.Email, x.Name, x.IsSuperAdmin, x.PasswordHash })
            .FirstOrDefaultAsync(cancellationToken);
        return u is null ? null : new UserAccess(u.Id, u.Email, u.Name, u.IsSuperAdmin, Passwords.Stamp(u.PasswordHash));
    }

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
        select new TenantAccess(t.Id, t.Name, m.Role, t.Status, t.ExpiresAt);
}
