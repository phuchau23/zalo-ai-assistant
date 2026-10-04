using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Zalo;

/// <summary>Kết quả hoàn tất kết nối: tenant lấy từ state (không từ query của Zalo).</summary>
public sealed record ZaloConnectResult(Guid TenantId, Guid ConnectionId, string OaId);

/// <summary>
/// Kết nối Zalo OA (OAuth v4 + PKCE) và giữ access token còn hạn (docs/zalo-api-notes.md mục 2–3).
/// - state + code_verifier + tenant + user lưu Redis 10 phút, lấy ra là xóa (dùng 1 lần, chống giả mạo callback).
/// - Làm mới token dưới khóa Redis theo kết nối: refresh token chỉ dùng 1 lần, access token cũ chết ngay khi có token mới →
///   lấy được khóa thì ĐỌC LẠI từ DB (bản khác có thể vừa làm mới xong), chỉ gọi Zalo khi vẫn cần.
/// Token chỉ tồn tại dạng rõ trong bộ nhớ khi dùng; lưu DB bằng IFieldEncryptor; không log.
/// </summary>
public sealed partial class ZaloConnectionService(
    ZaloClient zalo,
    IDistributedStore store,
    IFieldEncryptor encryptor,
    TenantContext tenantContext,
    ChannelConnectionRepository connections,
    ChannelConnectionLookup lookup,
    AuditLogRepository audit,
    TimeProvider time,
    IOptions<ZaloOptions> options,
    ILogger<ZaloConnectionService> logger)
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(60);

    /// <summary>Hạn refresh token theo docs (3 tháng kể từ lần cấp).</summary>
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(90);

    private sealed record PendingConnect(Guid TenantId, Guid UserId, string Verifier);

    /// <summary>Link trang cấp quyền của Zalo cho chủ DN mở.</summary>
    public async Task<string> StartAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var verifier = ZaloPkce.NewVerifier();
        var state = ZaloPkce.NewState();
        var url = zalo.BuildPermissionUrl(ZaloPkce.Challenge(verifier), state); // ném ZaloNotConfiguredException trước khi lưu gì
        await store.SetIfNotExistsAsync(StateKey(state), JsonSerializer.Serialize(new PendingConnect(tenantId, userId, verifier)), StateTtl, cancellationToken);
        return url;
    }

    /// <summary>
    /// Zalo gọi về callback: kiểm state (một lần, còn hạn) → đổi code lấy token → lưu/cập nhật kết nối của đúng tenant trong state.
    /// OA đang thuộc DN khác → từ chối (không lộ DN nào).
    /// </summary>
    public async Task<ZaloConnectResult> CompleteAsync(string state, string code, string oaId, CancellationToken cancellationToken)
    {
        var raw = await store.TakeAsync(StateKey(state), cancellationToken)
            ?? throw new InvalidInputException("Liên kết kết nối đã hết hạn hoặc đã dùng. Vui lòng bấm \"Kết nối Zalo OA\" lại.");
        var pending = JsonSerializer.Deserialize<PendingConnect>(raw)
            ?? throw new InvalidInputException("Liên kết kết nối không hợp lệ.");

        if (await lookup.BelongsToOtherTenantAsync(ChannelKind.Zalo, oaId, pending.TenantId, cancellationToken))
        {
            throw new ConflictException("OA này đang kết nối với một doanh nghiệp khác trên hệ thống. Ngắt kết nối ở đó trước.", "oa_in_use");
        }

        var tokens = await zalo.ExchangeCodeAsync(code, pending.Verifier, cancellationToken);

        tenantContext.Set(pending.TenantId, pending.UserId, role: null, isSuperAdmin: false);
        var now = time.GetUtcNow();
        var connection = await connections.GetByExternalIdAsync(pending.TenantId, ChannelKind.Zalo, oaId, cancellationToken);
        if (connection is null)
        {
            connection = connections.Add(pending.TenantId, new ChannelConnection
            {
                Channel = ChannelKind.Zalo,
                ExternalId = oaId,
                AccessTokenEnc = "",
                RefreshTokenEnc = "",
                ConnectedByUserId = pending.UserId,
            });
        }

        Apply(connection, tokens, now);
        connection.ConnectedByUserId = pending.UserId;
        audit.Add(pending.TenantId, pending.UserId, "channel.zalo_connected", $"oa:{oaId}");
        await connections.SaveChangesAsync(cancellationToken);
        return new ZaloConnectResult(pending.TenantId, connection.Id, oaId);
    }

    /// <summary>
    /// Access token còn hạn của kết nối (tenant context đã set). <paramref name="forceRefresh"/>: Zalo vừa báo token hỏng (-216/-220).
    /// Kết nối không còn hoạt động → null.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(Guid tenantId, Guid connectionId, bool forceRefresh, CancellationToken cancellationToken)
    {
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active })
        {
            return null;
        }

        if (!forceRefresh && !NeedsRefresh(connection))
        {
            return encryptor.Decrypt(connection.AccessTokenEnc);
        }

        var refreshed = await RefreshAsync(tenantId, connectionId, forceRefresh, cancellationToken);
        return refreshed is { Status: ConnectionStatus.Active } ? encryptor.Decrypt(refreshed.AccessTokenEnc) : null;
    }

    /// <summary>Làm mới token dưới khóa. Trả kết nối sau cùng (đã tải lại từ DB), null nếu không tồn tại.</summary>
    public async Task<ChannelConnection?> RefreshAsync(Guid tenantId, Guid connectionId, bool force, CancellationToken cancellationToken)
    {
        var before = time.GetUtcNow();
        await using var held = await AcquireAsync($"zalo:refresh:{connectionId:N}", cancellationToken)
            ?? throw new ZaloTransientException("Đang có tiến trình khác làm mới token, thử lại sau."); // job sẽ retry

        // Đọc lại sau khi có khóa: có thể bản khác vừa làm mới.
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken);
        if (connection is not { Status: ConnectionStatus.Active })
        {
            return connection;
        }

        var refreshedByOther = connection.LastRefreshedAt is { } last && last >= before;
        if (refreshedByOther || (!force && !NeedsRefresh(connection)))
        {
            return connection;
        }

        try
        {
            var tokens = await zalo.RefreshAsync(encryptor.Decrypt(connection.RefreshTokenEnc), cancellationToken);
            Apply(connection, tokens, time.GetUtcNow());
            await connections.SaveChangesAsync(cancellationToken);
            return connection;
        }
        catch (ZaloApiException ex)
        {
            // Refresh token sai/hết hạn/OA thu hồi quyền: không tự cứu được, chủ DN phải kết nối lại.
            await MarkNeedsReauthAsync(connection, ex.ShortCode, cancellationToken);
            return connection;
        }
    }

    /// <summary>Kênh mất quyền: dừng bot cho OA này, chờ chủ DN kết nối lại.</summary>
    public async Task MarkNeedsReauthAsync(Guid tenantId, Guid connectionId, string code, CancellationToken cancellationToken)
    {
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken);
        if (connection is not null)
        {
            await MarkNeedsReauthAsync(connection, code, cancellationToken);
        }
    }

    private async Task MarkNeedsReauthAsync(ChannelConnection connection, string code, CancellationToken cancellationToken)
    {
        connection.Status = ConnectionStatus.NeedsReauth;
        connection.LastError = code;
        await connections.SaveChangesAsync(cancellationToken);
        LogNeedsReauth(logger, connection.Id, connection.TenantId, code);
    }

    private bool NeedsRefresh(ChannelConnection connection) =>
        connection.AccessTokenExpiresAt - time.GetUtcNow() < TimeSpan.FromMinutes(options.Value.RefreshBeforeExpiryMinutes);

    private void Apply(ChannelConnection connection, ZaloTokens tokens, DateTimeOffset now)
    {
        connection.AccessTokenEnc = encryptor.Encrypt(tokens.AccessToken);
        connection.RefreshTokenEnc = encryptor.Encrypt(tokens.RefreshToken);
        connection.AccessTokenExpiresAt = now.AddSeconds(tokens.ExpiresInSeconds);
        connection.RefreshTokenExpiresAt = now.Add(RefreshTokenLifetime);
        connection.LastRefreshedAt = now;
        connection.Status = ConnectionStatus.Active;
        connection.LastError = null;
    }

    /// <summary>Chờ tối đa ~10 giây nếu bản khác đang giữ khóa (thường là đang làm mới xong).</summary>
    private async Task<IAsyncDisposable?> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var held = await store.TryAcquireLockAsync(key, LockTtl, cancellationToken);
            if (held is not null)
            {
                return held;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), time, cancellationToken);
        }

        return null;
    }

    private static string StateKey(string state) => $"zalo:oauth-state:{state}";

    [LoggerMessage(Level = LogLevel.Error, Message = "Kết nối Zalo {ConnectionId} của tenant {TenantId} cần cấp quyền lại ({Code})")]
    private static partial void LogNeedsReauth(ILogger logger, Guid connectionId, Guid tenantId, string code);
}
