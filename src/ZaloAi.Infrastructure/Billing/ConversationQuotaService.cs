using System.Globalization;
using Hangfire;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Dashboard;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

/// <param name="Used">Hội thoại thật (khách khác nhau có nhắn, không tính Chat thử) từ đầu tháng giờ Việt Nam.</param>
/// <param name="Quota">Hạn mức đang áp dụng; null = không giới hạn.</param>
public sealed record QuotaUsage(int Used, int? Quota)
{
    public const double WarningRatio = 0.8;

    public bool Exceeded => Quota is { } q && Used >= q;

    public bool Warning => Quota is { } q && q > 0 && Used >= WarningAt(q);

    /// <summary>Mốc cảnh báo: 80% hạn mức, làm tròn lên (100 → 80; 5 → 4).</summary>
    public static int WarningAt(int quota) => (int)Math.Ceiling(quota * WarningRatio);
}

/// <summary>
/// Hạn mức hội thoại/tháng theo gói (CLAUDE.md M6 đợt B4, chủ dự án chốt 2026-10-14). Hội thoại được bot trả lời khi nằm trong N
/// hội thoại BẮT ĐẦU sớm nhất của tháng (N = hạn mức) — nên khách đã nhắn trong tháng luôn được trả lời, khách MỚI sau khi hết hạn mức
/// thì chuyển nhân viên. Chat thử không tính. Tới 80% và khi bắt đầu chặn: báo Telegram mỗi tháng một lần.
/// </summary>
public sealed class ConversationQuotaService(
    ConversationRepository conversations,
    IOptions<BillingOptions> billing,
    IDistributedStore store,
    IBackgroundJobClient jobs,
    TimeProvider time)
{
    /// <summary>Lý do chuyển nhân viên khi hết hạn mức (hiện ở hộp thư, Telegram).</summary>
    public const string HandoffReason = "quota_exceeded";

    public async Task<QuotaUsage> UsageAsync(Guid tenantId, Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var used = await conversations.CountActiveConversationsAsync(tenantId, MonthStart(), cancellationToken);
        return new QuotaUsage(used, billing.Value.QuotaFor(tenant.Plan, tenant.ConversationQuota));
    }

    /// <summary>true = bot được trả lời hội thoại này; false = hết hạn mức và đây là khách mới của tháng → chuyển nhân viên.</summary>
    public async Task<bool> AllowBotAsync(Guid tenantId, Tenant tenant, Conversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.IsTest || billing.Value.QuotaFor(tenant.Plan, tenant.ConversationQuota) is not { } quota)
        {
            return true;
        }

        if (await conversations.MonthRankAsync(tenantId, conversation.Id, MonthStart(), cancellationToken) is not { } rank)
        {
            return true;
        }

        var allowed = rank < quota;
        if (!allowed)
        {
            await NotifyOnceAsync(tenantId, QuotaNotifyJob.Exceeded, quota, quota, cancellationToken);
        }
        else if (rank + 1 >= QuotaUsage.WarningAt(quota))
        {
            await NotifyOnceAsync(tenantId, QuotaNotifyJob.Warning, rank + 1, quota, cancellationToken);
        }

        return allowed;
    }

    private async Task NotifyOnceAsync(Guid tenantId, string level, int used, int quota, CancellationToken cancellationToken)
    {
        var month = DashboardStats.Window(time.GetUtcNow(), 1).LastDay.ToString("yyyyMM", CultureInfo.InvariantCulture);
        if (await store.SetIfNotExistsAsync($"quota-notify:{tenantId:N}:{month}:{level}", "1", TimeSpan.FromDays(40), cancellationToken))
        {
            jobs.Enqueue<QuotaNotifyJob>(job => job.RunAsync(tenantId, level, used, quota, CancellationToken.None));
        }
    }

    private DateTimeOffset MonthStart() => DashboardStats.Window(time.GetUtcNow(), 1).MonthStart;
}
