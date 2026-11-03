using Hangfire;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Báo nhóm Telegram của DN khi dùng tới 80% hạn mức hội thoại tháng, và khi bắt đầu chuyển khách mới sang nhân viên.</summary>
[Queue(JobQueues.Default)]
public sealed class QuotaNotifyJob(
    TenantContext tenantContext,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    TelegramClient telegram,
    IOptions<AppOptions> app)
{
    public const string Warning = "warning";
    public const string Exceeded = "exceeded";

    public async Task RunAsync(Guid tenantId, string level, int used, int quota, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.TelegramChatId) || tenant is null)
        {
            return;
        }

        var text = level == Exceeded
            ? $"⛔ {tenant.Name} đã dùng hết {quota} hội thoại của tháng này. Từ giờ khách MỚI nhắn sẽ được chuyển sang nhân viên (bot vẫn trả lời khách đã nhắn trong tháng). Liên hệ để nâng gói."
            : $"⚠️ {tenant.Name} đã dùng {used}/{quota} hội thoại của tháng này. Khi hết hạn mức, khách mới sẽ được chuyển sang nhân viên thay vì bot trả lời.";
        await telegram.SendAsync(settings.TelegramChatId, $"{text}\nXem tổng quan: {app.Value.AdminUrl.TrimEnd('/')}/", cancellationToken);
    }
}
