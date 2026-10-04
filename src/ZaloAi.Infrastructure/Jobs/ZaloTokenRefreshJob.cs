using Hangfire;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.Infrastructure.Zalo;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Quét định kỳ (job hệ thống, mọi tenant): kết nối Zalo có access token sắp hết hạn → xếp job làm mới riêng cho từng kết nối,
/// chạy trong đúng tenant. Access token sống 25 giờ; làm mới trước <see cref="ZaloOptions.RefreshBeforeExpiryMinutes"/>.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class ZaloTokenSweepJob(ChannelConnectionLookup lookup, IBackgroundJobClient jobs, TimeProvider time, IOptions<ZaloOptions> options)
{
    public const string RecurringId = "zalo-token-refresh-sweep";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow().AddMinutes(options.Value.RefreshBeforeExpiryMinutes);
        foreach (var (connectionId, tenantId) in await lookup.ListExpiringAsync(before, cancellationToken))
        {
            jobs.Enqueue<ZaloTokenRefreshJob>(job => job.RunAsync(tenantId, connectionId, CancellationToken.None));
        }
    }
}

/// <summary>
/// Làm mới token của một kết nối (dưới khóa Redis trong ZaloConnectionService). Refresh token hỏng → kết nối chuyển
/// "cần cấp quyền lại" và ghi log lỗi (Sentry). Lỗi mạng → Hangfire thử lại.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class ZaloTokenRefreshJob(TenantContext tenantContext, ZaloConnectionService connections)
{
    public async Task RunAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        await connections.RefreshAsync(tenantId, connectionId, force: false, cancellationToken);
    }
}
