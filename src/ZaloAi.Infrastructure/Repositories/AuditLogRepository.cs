using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>
/// audit_logs không có global query filter (tenant_id có thể null, xem <see cref="AuditLog"/>),
/// nên MỌI truy vấn ở đây phải tự lọc tenant_id.
/// </summary>
public sealed class AuditLogRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public void Add(Guid tenantId, Guid? userId, string action, string? target = null)
    {
        EnsureTenant(tenantId);
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = userId, Action = action, Target = target });
    }

    /// <summary>
    /// Ghi sự kiện đăng nhập/đăng xuất, khi tenant context chưa (hoặc không còn) được set.
    /// tenantId/userId ở đây luôn do server xác định (sau khi kiểm mật khẩu / cookie), không bao giờ lấy từ input client.
    /// </summary>
    public void AddAuthEvent(Guid? tenantId, Guid? userId, string action) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = userId, Action = action });

    public async Task<IReadOnlyList<AuditLog>> ListAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.AuditLogs
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
    }
}
