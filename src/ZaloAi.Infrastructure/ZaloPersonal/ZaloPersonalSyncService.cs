using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

public sealed record SidecarRunning(Guid ConnectionId, string? State);

public sealed record SidecarAccount(Guid ConnectionId, JsonElement Session, string? ProxyUrl);

public sealed record SidecarSyncResult(bool LeaseGranted, IReadOnlyList<SidecarAccount> Accounts);

/// <summary>
/// Nhịp tim + "trạng thái mong muốn" cho sidecar (CONTRACT.md mục 4, /sync mỗi 60 giây). Chỉ MỘT bản sidecar được giao nick
/// (zca-js chỉ cho 1 listener/nick, 2 bản chạy cùng nick sẽ đá nhau): lease Redis 90 giây, bản đang giữ gia hạn mỗi lần gọi.
/// Phiên chỉ giải mã ở đây, ngay trước khi trả cho sidecar qua cổng nội bộ có chữ ký.
/// </summary>
public sealed partial class ZaloPersonalSyncService(
    IDistributedStore store,
    ZaloPersonalLookup lookup,
    IFieldEncryptor encryptor,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ZaloPersonalSyncService> logger)
{
    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(90);

    private const string LeaseKey = "zp:lease";

    public async Task<SidecarSyncResult> SyncAsync(string instance, IReadOnlyList<SidecarRunning> running, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Length > 200 || !await AcquireLeaseAsync(instance, cancellationToken))
        {
            LogLeaseDenied(logger, running.Count);
            return new(false, []);
        }

        var now = time.GetUtcNow();
        await lookup.TouchSeenAsync(running.Select(r => r.ConnectionId).ToList(), now, cancellationToken);
        await lookup.RecoverConnectedAsync(running.Where(r => r.State == "connected").Select(r => r.ConnectionId).ToList(), now, cancellationToken);

        var accounts = new List<SidecarAccount>();
        foreach (var target in await lookup.ListRunTargetsAsync(cancellationToken))
        {
            // Fail-closed: nick có proxy mà proxy hỏng, hoặc production bắt buộc proxy mà nick chưa có → KHÔNG chạy (không đi thẳng IP máy chủ).
            if ((target.HasProxy && target.ProxyEnc is null) || (options.Value.RequireProxy && !target.HasProxy))
            {
                continue;
            }

            // Một phiên hỏng (đổi khóa mã hóa, dữ liệu lỗi) không được làm hỏng cả lượt đồng bộ của mọi nick khác.
            try
            {
                using var doc = JsonDocument.Parse(encryptor.Decrypt(target.SessionEnc));
                var proxyUrl = target.ProxyEnc is null ? null : encryptor.Decrypt(target.ProxyEnc);
                accounts.Add(new SidecarAccount(target.ConnectionId, doc.RootElement.Clone(), proxyUrl));
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
            {
                LogBrokenSession(logger, target.ConnectionId, target.TenantId, ex.GetType().Name);
            }
        }

        return new(true, accounts);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Không đọc được phiên đăng nhập của nick {ConnectionId} (tenant {TenantId}): {ErrorType} — bỏ qua nick này")]
    private static partial void LogBrokenSession(ILogger logger, Guid connectionId, Guid tenantId, string errorType);

    private async Task<bool> AcquireLeaseAsync(string instance, CancellationToken cancellationToken)
    {
        var holder = await store.GetAsync(LeaseKey, cancellationToken);
        if (holder is null && await store.SetIfNotExistsAsync(LeaseKey, instance, LeaseTtl, cancellationToken))
        {
            return true;
        }

        holder ??= await store.GetAsync(LeaseKey, cancellationToken);
        if (holder != instance)
        {
            return false;
        }

        await store.SetAsync(LeaseKey, instance, LeaseTtl, cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sidecar Zalo cá nhân không được giao nick (bản khác đang giữ lease); nó báo đang chạy {Running} nick")]
    private static partial void LogLeaseDenied(ILogger logger, int running);
}
