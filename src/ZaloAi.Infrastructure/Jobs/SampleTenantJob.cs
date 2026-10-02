using Hangfire;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Job mẫu, làm khuôn cho mọi job có dữ liệu tenant (skill tenant-safe-feature bước 4):
/// - tham số đầu tiên là tenantId, do API/webhook xác định (không lấy từ input khách cuối);
/// - dòng đầu tiên set tenant context, sau đó mọi truy vấn bị lọc và chặn ghi sai tenant như ở API;
/// - Hangfire tạo DI scope riêng cho mỗi lần chạy, nên TenantContext không lẫn giữa các job.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class SampleTenantJob(TenantContext tenantContext, AuditLogRepository audit)
{
    public const string AuditAction = "job.sample_ran";

    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);

        audit.Add(tenantId, null, AuditAction);
        await audit.SaveChangesAsync(cancellationToken);
    }
}
