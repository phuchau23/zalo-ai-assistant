using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Billing;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Mỗi giờ (job HỆ THỐNG, IgnoreQueryFilters có chủ đích chỉ để lấy tenant có gói quá hạn) → đánh dấu gói hết hạn theo từng tenant.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class PackageExpiryJob(AppDbContext db, IServiceScopeFactory scopes, TimeProvider time)
{
    public const string RecurringId = "package-expiry";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var tenantIds = await db.CustomerPackages.IgnoreQueryFilters()
            .Where(p => p.Status == CustomerPackageStatus.Active && p.ExpiresAt != null && p.ExpiresAt < now)
            .Select(p => p.TenantId).Distinct().ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, null, null, isSuperAdmin: false);
            await scope.ServiceProvider.GetRequiredService<PackageService>().ExpireAsync(tenantId, cancellationToken);
        }
    }
}
