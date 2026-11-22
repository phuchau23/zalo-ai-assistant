using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Số liệu hàng chờ của một nick để soạn tin "sắp hết / đã hết danh sách".</summary>
/// <param name="Contacted">Người nick đã mời kết bạn hoặc nhắn tin (mọi đợt).</param>
public sealed record NickQueueStats(int Waiting, int EtaDays, int Contacted, int Replied, int Booked, int NoZalo);

/// <summary>Nội dung tin Telegram báo về nick Zalo cá nhân (hàm thuần, để test). Không có tên/SĐT khách — chỉ tên nick và con số.</summary>
public static class ZaloPersonalAlertText
{
    public static string Build(string reason, string nickName, string tenantName, string adminUrl, Guid connectionId, NickQueueStats? stats)
    {
        var url = adminUrl.TrimEnd('/');
        var nick = $"Nick Zalo cá nhân \"{nickName}\"";
        var nickPage = $"Mở trang nick: {url}/zalo-personal/{connectionId}";
        var addPage = $"{url}/zalo-personal?tab=add";
        return reason switch
        {
            OutreachQueueWatcher.QueueEmpty when stats is not null =>
                $"📋 {nick} đã nhắn hết danh sách — {tenantName}\n" +
                $"Đã liên hệ {stats.Contacted} người: {stats.Replied} trả lời, {stats.Booked} có lịch hẹn, {stats.NoZalo} không có Zalo.\n" +
                $"Dán thêm số để bot chăm sóc tiếp: {addPage}",
            OutreachQueueWatcher.QueueLow when stats is not null =>
                $"⏳ {nick} sắp hết danh sách: còn {stats.Waiting} người chờ, khoảng {Math.Max(1, stats.EtaDays)} ngày nữa là nhắn hết — {tenantName}\n" +
                $"Chuẩn bị thêm số: {addPage}",
            "zp:outreach_normal" =>
                $"✅ {nick} đã nhắn lại bình thường vì khách phản hồi tốt hơn — {tenantName}\n{nickPage}",
            "zp:outreach_slow" =>
                $"🐢 {nick} đang nhắn chậm lại vì nhiều người từ chối / không chấp nhận kết bạn. Xem lại danh sách SĐT hoặc lời chào — {tenantName}\n{nickPage}",
            "zp:outreach_paused" =>
                $"🐢 {nick} đã DỪNG nhắn trước 72 giờ vì nhiều người từ chối / chặn (để giữ nick). Vẫn trả lời khách tự nhắn tới — {tenantName}\n{nickPage}",
            _ => $"📵 {nick} {Problem(reason)}. Trong lúc này bot không trả lời khách qua nick đó — {tenantName}\n{nickPage}",
        };
    }

    private static string Problem(string reason) => reason switch
    {
        "zp:opened_elsewhere" => "vừa mở Zalo trên trình duyệt ở nơi khác nên bot bị ngắt. Đóng Zalo Web rồi bấm \"Kết nối lại\"",
        "zp:session_expired" => "hết phiên đăng nhập. Vào trang Zalo cá nhân, quét lại mã QR",
        "zp:proxy_failing" => "đang tạm dừng vì đường kết nối riêng (IP) gặp sự cố. Remi đang xử lý",
        _ => "đang mất kết nối",
    };
}

/// <summary>
/// Báo chủ DN qua Telegram về một nick Zalo cá nhân: cần xử lý (mở Zalo Web nơi khác, phiên hết hạn, proxy hỏng), phanh tự động
/// chậm lại / tạm dừng / chạy lại bình thường, danh sách SĐT sắp hết / đã hết (2026-11-17). Số liệu hàng chờ tính lúc gửi.
/// </summary>
[Queue(JobQueues.Default)]
public sealed class ZaloPersonalAlertJob(
    TenantContext tenantContext,
    ChannelConnectionRepository connections,
    TenantRepository tenants,
    HandoffSettingsRepository handoffSettings,
    OutreachRepository outreach,
    ZaloPersonalRepository personal,
    TelegramClient telegram,
    IOptions<AppOptions> app,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time)
{
    public async Task RunAsync(Guid tenantId, Guid connectionId, string reason, CancellationToken cancellationToken)
    {
        tenantContext.Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.TelegramChatId) || connection is null || tenant is null)
        {
            return;
        }

        NickQueueStats? stats = null;
        if (reason is OutreachQueueWatcher.QueueLow or OutreachQueueWatcher.QueueEmpty)
        {
            var counts = (await outreach.StateCountsAsync(tenantId, cancellationToken)).Where(r => r.ConnectionId == connectionId).ToDictionary(r => r.State, r => r.Count);
            int Count(params OutreachState[] states) => states.Sum(s => counts.GetValueOrDefault(s));
            var waiting = await outreach.CountQueuedAsync(tenantId, connectionId, cancellationToken);
            var account = await personal.GetAccountAsync(tenantId, connectionId, cancellationToken);
            var perDay = account is null ? 1 : Math.Max(1, PersonalPacing.Effective(account, options.Value, time.GetUtcNow()).NewContactsPerDay);
            stats = new NickQueueStats(
                waiting,
                OutreachAllocator.Eta(waiting, perDay),
                Count(OutreachState.Invited, OutreachState.Friend, OutreachState.Messaged, OutreachState.Replied, OutreachState.NoReply, OutreachState.Declined, OutreachState.Expired),
                Count(OutreachState.Replied),
                (await outreach.BookedCountsAsync(tenantId, time.GetUtcNow(), cancellationToken)).GetValueOrDefault(connectionId),
                Count(OutreachState.NoZalo));
        }

        await telegram.SendAsync(
            settings.TelegramChatId,
            ZaloPersonalAlertText.Build(reason, connection.Name ?? "Zalo cá nhân", tenant.Name, app.Value.AdminUrl, connectionId, stats),
            cancellationToken);
    }
}

/// <summary>
/// Mỗi phút: nick đáng lẽ đang chạy mà quá hạn không thấy nhịp tim (sidecar tắt/treo) → đánh dấu "mất liên lạc" để giao diện báo
/// đúng. Đây là sự cố của hệ thống, không phải của DN → ghi log Error (Sentry báo chủ dự án), không nhắn Telegram cho DN.
/// </summary>
[Queue(JobQueues.Default)]
public sealed partial class ZaloPersonalHealthJob(
    ZaloPersonalLookup lookup,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ZaloPersonalHealthJob> logger)
{
    public const string RecurringId = "zalo-personal-health";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            return;
        }

        var now = time.GetUtcNow();
        var stale = await lookup.ListStaleAsync(now.AddMinutes(-options.Value.StaleAfterMinutes), cancellationToken);
        if (stale.Count == 0)
        {
            return;
        }

        await lookup.MarkStaleAsync(stale.Select(s => s.ConnectionId).ToList(), now, cancellationToken);
        LogStale(logger, stale.Count, options.Value.StaleAfterMinutes);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Count} nick Zalo cá nhân quá {Minutes} phút không có nhịp tim từ sidecar — kiểm tra service connectors/zalo-personal")]
    private static partial void LogStale(ILogger logger, int count, int minutes);
}

/// <summary>
/// Mỗi 5 phút kiểm tra proxy qua sidecar (docs/ZALO-PERSONAL-PLAN.md mục 8). Hỏng 2 lần liền → nick dùng proxy đó DỪNG (không bao giờ
/// chạy thẳng bằng IP máy chủ), báo chủ DN + log Error cho super admin. Proxy khỏe lại → nick tự chạy lại. Sidecar không chạy → bỏ lượt
/// (không đổ lỗi cho proxy). Job hệ thống nhiều tenant → cập nhật nick bằng ExecuteUpdate qua ZaloPersonalLookup.
/// </summary>
[Queue(JobQueues.Default)]
public sealed partial class ProxyHealthJob(
    AppDbContext db,
    ProxyPoolService pool,
    SidecarClient sidecar,
    IFieldEncryptor encryptor,
    ZaloPersonalLookup lookup,
    IBackgroundJobClient jobs,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ProxyHealthJob> logger)
{
    public const string RecurringId = "zalo-personal-proxy-health";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            return;
        }

        var proxies = await db.Proxies.Where(p => p.Status != ProxyStatus.Retired).ToListAsync(cancellationToken);
        foreach (var proxy in proxies)
        {
            SidecarProxyTest result;
            try
            {
                result = await sidecar.TestProxyAsync(encryptor.Decrypt(proxy.CredentialsEnc), cancellationToken);
            }
            catch (ChannelUnavailableException)
            {
                LogSidecarDown(logger);
                return;
            }

            if (!pool.Record(proxy, result))
            {
                continue;
            }

            await db.SaveChangesAsync(cancellationToken);
            var now = time.GetUtcNow();
            if (proxy.Status == ProxyStatus.Failing)
            {
                foreach (var (connectionId, tenantId) in await lookup.StopForProxyAsync(proxy.Id, now, cancellationToken))
                {
                    jobs.Enqueue<ZaloPersonalAlertJob>(job => job.RunAsync(tenantId, connectionId, "zp:proxy_failing", CancellationToken.None));
                }

                LogProxyFailing(logger, proxy.Id, proxy.FailCount);
            }
            else
            {
                await lookup.ResumeForProxyAsync(proxy.Id, now, cancellationToken);
                LogProxyRecovered(logger, proxy.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (options.Value.RequireProxy && await lookup.CountFreeProxiesAsync(time.GetUtcNow(), cancellationToken) < 2)
        {
            LogPoolLow(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không gọi được sidecar để kiểm tra proxy — bỏ lượt này")]
    private static partial void LogSidecarDown(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proxy {ProxyId} hỏng {FailCount} lần liền — các nick dùng proxy này đã dừng, cần đổi proxy (trang super admin)")]
    private static partial void LogProxyFailing(ILogger logger, Guid proxyId, int failCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proxy {ProxyId} đã khỏe lại — nick dùng proxy này chạy lại")]
    private static partial void LogProxyRecovered(ILogger logger, Guid proxyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kho proxy Zalo cá nhân còn dưới 2 proxy trống — nhập thêm trước khi DN mới kết nối nick")]
    private static partial void LogPoolLow(ILogger logger);
}
