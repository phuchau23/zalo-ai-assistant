using System.Globalization;
using Hangfire;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Nội dung tin Telegram "hôm nay có N số cần gọi" (hàm thuần, để test). Chỉ con số + link, không tên/SĐT khách.</summary>
public static class CallListText
{
    public static string Build(int total, int warm, string tenantName, string adminUrl)
    {
        var url = adminUrl.TrimEnd('/');
        var warmNote = warm > 0 ? $" ({warm} người từng tư vấn rồi im — nên gọi trước)" : "";
        return $"📞 Hôm nay có {total} số cần gọi điện{warmNote} — {tenantName}\n" +
               "Bot đã nhắn nhưng khách chưa trả lời, hoặc khách không có Zalo. Gọi xong nhớ bấm kết quả.\n" +
               $"Mở danh sách: {url}/care#call-list";
    }
}

/// <summary>
/// Mỗi ngày MỘT tin Telegram báo số người cần gọi (chủ dự án chốt 2026-11-25), lúc job dọn dẹp chạy lần đầu trong khung giờ được nhắn của
/// DN (job chạy phút 20 mỗi giờ → vd 08:20). Cần DN đã kết nối Telegram; danh sách trống thì không báo.
/// </summary>
public sealed class CallListDigest(
    OutreachRepository outreach,
    HandoffSettingsRepository handoffSettings,
    IDistributedStore store,
    IBackgroundJobClient jobs,
    TimeProvider time)
{
    /// <returns>true nếu vừa xếp tin gửi.</returns>
    public async Task<bool> CheckAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        if (string.IsNullOrWhiteSpace(settings.TelegramChatId) || !CareDecision.InSendWindow(settings, now))
        {
            return false;
        }

        var (total, _) = await outreach.CountCallListAsync(tenantId, cancellationToken);
        if (total == 0)
        {
            return false;
        }

        var day = now.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (!await store.SetIfNotExistsAsync($"care:call-digest:{tenantId:N}:{day}", "1", TimeSpan.FromDays(2), cancellationToken))
        {
            return false; // hôm nay đã báo
        }

        jobs.Enqueue<CallListDigestJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        return true;
    }
}

/// <summary>Gửi tin "hôm nay có N số cần gọi" (số liệu tính lúc gửi). Lỗi mạng → Hangfire thử lại.</summary>
[Queue(JobQueues.Default)]
public sealed class CallListDigestJob(
    TenantContext tenantContext,
    OutreachRepository outreach,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    TelegramClient telegram,
    IOptions<AppOptions> app)
{
    public async Task RunAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.TelegramChatId) || tenant is null)
        {
            return;
        }

        var (total, warm) = await outreach.CountCallListAsync(tenantId, cancellationToken);
        if (total == 0)
        {
            return;
        }

        await telegram.SendAsync(settings.TelegramChatId, CallListText.Build(total, warm, tenant.Name, app.Value.AdminUrl), cancellationToken);
    }
}
