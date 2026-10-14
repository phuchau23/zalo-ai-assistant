using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Admin;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Job HỆ THỐNG mỗi giờ (chủ dự án chốt 2026-10-14): DN quá hạn thuê hơn <see cref="BillingOptions.SuspendAfterExpiryDays"/> ngày
/// mà chưa gia hạn → tự khóa (bot ngừng, người của DN không vào được web). Chạy với quyền super admin để đọc danh sách mọi DN;
/// việc khóa từng DN đi qua <see cref="TenantAdminService"/> (đặt tenant context đúng DN, ghi audit log).
/// </summary>
[Queue(JobQueues.Default)]
public sealed partial class TenantExpiryJob(
    TenantContext tenantContext,
    SuperAdminRepository superAdmin,
    TenantAdminService admin,
    IOptions<BillingOptions> billing,
    TimeProvider time,
    ILogger<TenantExpiryJob> logger)
{
    public const string RecurringId = "tenant-expiry";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        tenantContext.Set(null, null, null, isSuperAdmin: true);
        var cutoff = time.GetUtcNow().AddDays(-billing.Value.SuspendAfterExpiryDays);
        var expired = await superAdmin.ListExpiredActiveAsync(cutoff, cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        var suspended = await admin.SuspendExpiredAsync(expired, cancellationToken);
        LogSuspended(logger, suspended);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tự khóa {Count} doanh nghiệp quá hạn thuê")]
    private static partial void LogSuspended(ILogger logger, int count);
}
