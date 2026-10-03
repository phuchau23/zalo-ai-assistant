using Hangfire;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Tạo vector cho mọi đoạn đang chờ của một tenant. Chạy lại nhiều lần vô hại (chỉ xử lý đoạn chưa có vector).</summary>
[Queue(JobQueues.Default)]
public sealed class IndexKnowledgeJob(TenantContext tenantContext, KnowledgeIndexer indexer)
{
    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        await indexer.IndexPendingAsync(tenantId, cancellationToken);
    }
}

/// <summary>
/// Lưới an toàn chạy định kỳ: tìm tenant còn đoạn chưa có vector (ví dụ job trước hết lượt thử lại) và xếp lại job đánh chỉ mục.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class IndexSweepJob(AppDbContext db, IBackgroundJobClient jobs)
{
    public const string RecurringId = "knowledge-index-sweep";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Job hệ thống, chạy ngoài mọi tenant: IgnoreQueryFilters có chủ đích, chỉ đọc tenant_id (không đọc nội dung),
        // rồi giao việc cho IndexKnowledgeJob chạy trong đúng tenant.
        var tenantIds = await db.Chunks
            .IgnoreQueryFilters()
            .Where(c => c.Embedding == null)
            .Select(c => c.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var tenantId in tenantIds)
        {
            jobs.Enqueue<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        }
    }
}
