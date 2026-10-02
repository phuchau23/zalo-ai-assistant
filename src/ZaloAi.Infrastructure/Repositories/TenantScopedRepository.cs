using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>
/// Lớp cha cho repository dữ liệu tenant. Mọi hàm public nhận <c>Guid tenantId</c> đầu tiên, gọi <see cref="EnsureTenant"/>,
/// và vẫn lọc <c>tenant_id</c> tường minh trong truy vấn (không chỉ dựa vào global query filter).
/// </summary>
public abstract class TenantScopedRepository(AppDbContext db, ITenantContext tenantContext)
{
    protected AppDbContext Db { get; } = db;

    /// <summary>tenantId truyền vào phải khớp tenant của request/job. Lệch nhau = lỗi lập trình.</summary>
    protected void EnsureTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantContext.TenantId != tenantId)
        {
            throw new TenantIsolationException("tenantId không khớp tenant hiện tại.");
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Db.SaveChangesAsync(cancellationToken);
}
