using Hangfire;
using ZaloAi.Infrastructure.Billing;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// 23:30 mỗi đêm (giờ VN, sau khi Vietcombank chốt giá lúc 23:00): lấy lại giá bán USD hôm nay + hôm qua, bù các ngày còn thiếu trong
/// 400 ngày qua (đủ cho biểu đồ 12 tháng của super admin) — để báo cáo chi phí quy ra VND đúng tỷ giá từng ngày. Bảng hệ thống, không thuộc tenant.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class ExchangeRateSyncJob(ExchangeRateService rates)
{
    public const string RecurringId = "exchange-rate-sync";

    public Task RunAsync(CancellationToken cancellationToken) => rates.SyncAsync(backfillDays: 400, cancellationToken);
}
