using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Trạng thái phiên đăng nhập QR (giữ trong Redis 6 phút, gắn tenant + người bấm).</summary>
/// <param name="State">starting | qr | scanned | success | expired | declined | error</param>
public sealed record PersonalLoginState(
    Guid TenantId,
    Guid UserId,
    string UserAgent,
    string State,
    string? QrPng = null,
    Guid? ConnectionId = null,
    string? Error = null,
    string? DisplayName = null,
    Guid? ProxyId = null);

/// <summary>Kết quả nhận phiên đăng nhập từ sidecar.</summary>
/// <param name="Error">expired | already_connected_elsewhere | limit_reached | invalid_session; null = thành công.</param>
public sealed record PersonalLoginOutcome(Guid? ConnectionId, string? Error);

/// <summary>
/// Kết nối nick Zalo cá nhân bằng QR (docs/ZALO-PERSONAL-PLAN.md mục 6.1). Không bao giờ hỏi mật khẩu Zalo. Phiên đăng nhập (toàn
/// quyền nick) mã hóa trước khi lưu, không log, không trả ra API. Một nick chỉ thuộc một DN (unique channel + external_id).
/// </summary>
public sealed partial class ZaloPersonalLoginService(
    IDistributedStore store,
    SidecarClient sidecar,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    ChannelConnectionLookup lookup,
    AuditLogRepository audit,
    TenantContext tenantContext,
    IFieldEncryptor encryptor,
    ProxyPoolService proxies,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ZaloPersonalLoginService> logger)
{
    public static readonly TimeSpan LoginTtl = TimeSpan.FromMinutes(6);

    /// <summary>
    /// userAgent trình duyệt thật cho nick mới. zca-js tính imei từ userAgent → một nick giữ một userAgent mãi (đổi = thiết bị lạ).
    /// </summary>
    internal static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="connectionId">Quét lại QR cho nick đã có (phiên hết hạn): dùng lại ĐÚNG userAgent + proxy cũ để Zalo thấy cùng một
    /// thiết bị, cùng một IP. null = nick mới (userAgent ngẫu nhiên trong danh sách, giữ chỗ một proxy trống).</param>
    public async Task<string> StartAsync(Guid tenantId, Guid userId, Guid? connectionId, CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            throw new ConflictException("Hệ thống chưa cấu hình kết nối Zalo cá nhân. Liên hệ quản trị viên.", "zalo_personal_not_configured");
        }

        string userAgent;
        Guid? proxyId;
        string? proxyUrl;
        if (connectionId is { } existingId)
        {
            var account = await personal.GetAccountAsync(tenantId, existingId, cancellationToken) ?? throw new NotFoundException();
            userAgent = UserAgentOf(account) ?? UserAgents[RandomNumberGenerator.GetInt32(UserAgents.Length)];
            proxyId = account.ProxyId;
            proxyUrl = await proxies.UrlIfUsableAsync(proxyId, cancellationToken);
            if (proxyId is not null && proxyUrl is null)
            {
                throw new ConflictException("Đường kết nối riêng (IP) của nick đang lỗi. Remi đang xử lý, bạn thử lại sau.", "zalo_personal_proxy_unusable");
            }
        }
        else
        {
            if (await personal.CountAccountsAsync(tenantId, cancellationToken) >= options.Value.MaxAccountsPerTenant)
            {
                throw new ConflictException(
                    $"Đã đạt số nick Zalo cá nhân tối đa ({options.Value.MaxAccountsPerTenant}). Ngắt một nick trước khi thêm nick mới.",
                    "zalo_personal_limit");
            }

            userAgent = UserAgents[RandomNumberGenerator.GetInt32(UserAgents.Length)];
            var reserved = await proxies.ReserveFreeAsync(cancellationToken);
            if (reserved is null && options.Value.RequireProxy)
            {
                throw new ConflictException("Hết đường kết nối riêng (IP) cho nick mới. Liên hệ Remi để được cấp thêm.", "zalo_personal_no_proxy");
            }

            proxyId = reserved?.ProxyId;
            proxyUrl = reserved?.ProxyUrl;
        }

        // Quét QR đi qua ĐÚNG proxy sẽ dùng về sau: đăng nhập, nhận tin, gửi tin cùng một IP.
        var loginId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await SaveAsync(loginId, new PersonalLoginState(tenantId, userId, userAgent, "starting", ProxyId: proxyId), cancellationToken);
        await sidecar.StartLoginAsync(loginId, userAgent, proxyUrl, cancellationToken);
        return loginId;
    }

    private string? UserAgentOf(ZaloPersonalAccount account)
    {
        try
        {
            using var doc = JsonDocument.Parse(encryptor.Decrypt(account.SessionEnc));
            return doc.RootElement.TryGetProperty("userAgent", out var ua) && ua.ValueKind == JsonValueKind.String ? ua.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Trạng thái cho FE hỏi định kỳ. Phiên của DN khác / hết hạn → null (404, không lộ sự tồn tại).</summary>
    public async Task<PersonalLoginState?> GetAsync(Guid tenantId, string loginId, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(loginId, cancellationToken);
        return state is not null && state.TenantId == tenantId ? state : null;
    }

    public async Task CancelAsync(Guid tenantId, string loginId, CancellationToken cancellationToken)
    {
        if (await GetAsync(tenantId, loginId, cancellationToken) is null)
        {
            throw new NotFoundException();
        }

        await store.SetAsync(Key(loginId), "", TimeSpan.FromSeconds(1), cancellationToken);
        await sidecar.CancelLoginAsync(loginId, cancellationToken);
    }

    /// <summary>Sidecar báo tiến độ QR. false = phiên không còn (sidecar dừng đăng nhập đó).</summary>
    public async Task<bool> OnProgressAsync(string loginId, string state, string? qrPng, string? error, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(loginId, cancellationToken);
        if (current is null || current.State is "success")
        {
            return false;
        }

        var next = state switch
        {
            "qr" => current with { State = "qr", QrPng = qrPng },
            "scanned" => current with { State = "scanned", QrPng = null },
            "expired" or "declined" => current with { State = state, QrPng = null },
            _ => current with { State = "error", QrPng = null, Error = Short(error) },
        };
        await SaveAsync(loginId, next, cancellationToken);
        return true;
    }

    /// <summary>Quét QR xong: lưu nick (phiên mã hóa) vào DN đã bấm kết nối. Chạy trong request nội bộ, chưa có tenant context.</summary>
    public async Task<PersonalLoginOutcome> OnResultAsync(
        string loginId, string uid, string? displayName, JsonElement session, CancellationToken cancellationToken)
    {
        var state = await LoadAsync(loginId, cancellationToken);
        if (state is null || state.State is "success" or "expired" or "declined")
        {
            return new(null, "expired");
        }

        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 100 || !IsValidSession(session))
        {
            await SaveAsync(loginId, state with { State = "error", Error = "Phiên đăng nhập không hợp lệ." }, cancellationToken);
            return new(null, "invalid_session");
        }

        var tenantId = state.TenantId;
        tenantContext.Set(tenantId, state.UserId, TenantRole.Owner, isSuperAdmin: false);

        if (await lookup.BelongsToOtherTenantAsync(ChannelKind.ZaloPersonal, uid, tenantId, cancellationToken))
        {
            await SaveAsync(loginId, state with { State = "error", Error = "Nick Zalo này đang kết nối với một doanh nghiệp khác." }, cancellationToken);
            return new(null, "already_connected_elsewhere");
        }

        var now = time.GetUtcNow();
        var name = Short(displayName);
        var connection = await connections.GetByExternalIdAsync(tenantId, ChannelKind.ZaloPersonal, uid, cancellationToken);
        if (connection is null)
        {
            if (await personal.CountAccountsAsync(tenantId, cancellationToken) >= options.Value.MaxAccountsPerTenant)
            {
                await SaveAsync(loginId, state with { State = "error", Error = "Đã đạt số nick Zalo cá nhân tối đa." }, cancellationToken);
                return new(null, "limit_reached");
            }

            connection = connections.Add(tenantId, new ChannelConnection
            {
                Channel = ChannelKind.ZaloPersonal,
                ExternalId = uid,
                Name = name,
                AccessTokenEnc = "", // cột của OA, kênh cá nhân không dùng
                RefreshTokenEnc = "",
                AccessTokenExpiresAt = now,
                RefreshTokenExpiresAt = now,
                ConnectedByUserId = state.UserId,
            });
            personal.AddAccount(tenantId, new ZaloPersonalAccount
            {
                ConnectionId = connection.Id,
                SessionEnc = encryptor.Encrypt(session.GetRawText()),
                SessionSavedAt = now,
                ListenerState = ListenerState.Starting,
                ListenerStateChangedAt = now,
                ProxyId = state.ProxyId, // proxy đã giữ chỗ lúc bắt đầu, QR đã quét qua đúng proxy này
            });
        }
        else
        {
            // Quét lại QR cho nick đã có (phiên hết hạn): thay phiên, chạy lại. Giữ cài đặt, người quen, proxy.
            var account = await personal.GetAccountAsync(tenantId, connection.Id, cancellationToken);
            if (account is null)
            {
                personal.AddAccount(tenantId, account = new ZaloPersonalAccount { ConnectionId = connection.Id, SessionEnc = "" });
            }

            account.ProxyId ??= state.ProxyId;
            account.SessionEnc = encryptor.Encrypt(session.GetRawText());
            account.SessionSavedAt = now;
            account.ListenerState = ListenerState.Starting;
            account.ListenerStateChangedAt = now;
            account.LastStateCode = null;
            connection.Status = ConnectionStatus.Active;
            connection.LastError = null;
            connection.Name = name ?? connection.Name;
        }

        audit.Add(tenantId, state.UserId, "channel.connected", $"zalopersonal:{uid}");
        await connections.SaveChangesAsync(cancellationToken);
        await SaveAsync(loginId, state with { State = "success", QrPng = null, ConnectionId = connection.Id, DisplayName = name }, cancellationToken);
        LogConnected(logger, connection.Id, tenantId);
        return new(connection.Id, null);
    }

    /// <summary>Phiên zca-js phải có cookie, imei, userAgent (CONTRACT.md mục 2).</summary>
    public static bool IsValidSession(JsonElement session) =>
        session.ValueKind == JsonValueKind.Object
        && session.TryGetProperty("cookie", out var cookie) && cookie.ValueKind is JsonValueKind.Array or JsonValueKind.Object
        && session.TryGetProperty("imei", out var imei) && imei.ValueKind == JsonValueKind.String && imei.GetString() is { Length: > 0 }
        && session.TryGetProperty("userAgent", out var ua) && ua.ValueKind == JsonValueKind.String && ua.GetString() is { Length: > 0 };

    private async Task<PersonalLoginState?> LoadAsync(string loginId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loginId) || loginId.Length > 64)
        {
            return null;
        }

        var raw = await store.GetAsync(Key(loginId), cancellationToken);
        return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<PersonalLoginState>(raw, Json);
    }

    private Task SaveAsync(string loginId, PersonalLoginState state, CancellationToken cancellationToken) =>
        store.SetAsync(Key(loginId), JsonSerializer.Serialize(state, Json), LoginTtl, cancellationToken);

    private static string Key(string loginId) => $"zp:login:{loginId}";

    private static string? Short(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > 200 ? value.Trim()[..200] : value.Trim();

    [LoggerMessage(Level = LogLevel.Information, Message = "Đã kết nối nick Zalo cá nhân {ConnectionId} cho tenant {TenantId}")]
    private static partial void LogConnected(ILogger logger, Guid connectionId, Guid tenantId);
}
