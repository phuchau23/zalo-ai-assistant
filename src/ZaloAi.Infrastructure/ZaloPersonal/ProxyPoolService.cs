using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Một proxy kèm nick đang dùng (cho trang super admin). Không bao giờ có mật khẩu proxy.</summary>
public sealed record ProxyView(
    Guid Id,
    string Label,
    string Endpoint,
    string? Country,
    string? ExitIp,
    ProxyStatus Status,
    int FailCount,
    DateTimeOffset? LastCheckedAt,
    string? LastError,
    DateTimeOffset? CoolingUntil,
    Guid? ConnectionId,
    string? NickName,
    string? TenantName);

/// <summary>Một nick Zalo cá nhân (mọi DN) cho trang super admin.</summary>
public sealed record PersonalNickView(
    Guid ConnectionId, Guid TenantId, string TenantName, string? NickName, ConnectionStatus Status, ListenerState ListenerState, Guid? ProxyId);

/// <summary>
/// Kho proxy IP riêng cho nick Zalo cá nhân (docs/ZALO-PERSONAL-PLAN.md mục 8). Bảng hệ thống: CHỈ super admin (API gắn
/// RequireSuperAdmin) và hệ thống (cấp proxy lúc đăng nhập QR) dùng — IgnoreQueryFilters() có chủ đích vì liệt kê nick mọi DN.
/// Một proxy ↔ một nick; thả ra thì nghỉ vài ngày; hỏng thì nick dừng (không bao giờ chạy thẳng bằng IP máy chủ).
/// </summary>
public sealed partial class ProxyPoolService(
    AppDbContext db,
    IFieldEncryptor encryptor,
    IDistributedStore store,
    SidecarClient sidecar,
    IOptions<SecurityOptions> security,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ProxyPoolService> logger)
{
    private static readonly TimeSpan ReserveTtl = TimeSpan.FromMinutes(7);

    public async Task<IReadOnlyList<ProxyView>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await (from p in db.Proxies
                          join a in db.ZaloPersonalAccounts.IgnoreQueryFilters() on p.Id equals a.ProxyId into accounts
                          from a in accounts.DefaultIfEmpty()
                          join c in db.ChannelConnections.IgnoreQueryFilters() on a.ConnectionId equals c.Id into connections
                          from c in connections.DefaultIfEmpty()
                          join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id into tenants
                          from t in tenants.DefaultIfEmpty()
                          orderby p.CreatedAt
                          select new ProxyView(
                              p.Id, p.Label, p.Endpoint, p.Country, p.ExitIp, p.Status, p.FailCount, p.LastCheckedAt, p.LastError, p.CoolingUntil,
                              a == null ? null : a.ConnectionId, c == null ? null : c.Name, t == null ? null : t.Name))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<PersonalNickView>> ListNicksAsync(CancellationToken cancellationToken) =>
        await (from a in db.ZaloPersonalAccounts.IgnoreQueryFilters()
               join c in db.ChannelConnections.IgnoreQueryFilters() on a.ConnectionId equals c.Id
               join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
               orderby t.Name, c.CreatedAt
               select new PersonalNickView(c.Id, c.TenantId, t.Name, c.Name, c.Status, a.ListenerState, a.ProxyId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    /// <summary>Thêm proxy. URL dạng http(s)://user:pass@host:port. Trùng (dấu vân tay) → 409.</summary>
    public async Task<Proxy> AddAsync(string label, string url, string? country, CancellationToken cancellationToken)
    {
        var (normalized, endpoint) = Normalize(url);
        var fingerprint = Fingerprint(normalized);
        if (await db.Proxies.AnyAsync(p => p.Fingerprint == fingerprint, cancellationToken))
        {
            throw new ConflictException("Proxy này đã có trong kho.", "proxy_duplicate");
        }

        var proxy = new Proxy
        {
            Id = Guid.CreateVersion7(),
            Label = label.Trim(),
            CredentialsEnc = encryptor.Encrypt(normalized),
            Fingerprint = fingerprint,
            Endpoint = endpoint,
            Country = string.IsNullOrWhiteSpace(country) ? null : country.Trim(),
        };
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync(cancellationToken);
        return proxy;
    }

    /// <summary>Kiểm tra ngay một proxy qua sidecar; cập nhật IP ra / trạng thái. Sidecar không chạy → 503 (không đánh dấu hỏng).</summary>
    public async Task<SidecarProxyTest> TestAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken) ?? throw new NotFoundException();
        var result = await sidecar.TestProxyAsync(encryptor.Decrypt(proxy.CredentialsEnc), cancellationToken);
        Record(proxy, result);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task RetireAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken) ?? throw new NotFoundException();
        if (await db.ZaloPersonalAccounts.IgnoreQueryFilters().AnyAsync(a => a.ProxyId == proxyId, cancellationToken))
        {
            throw new ConflictException("Proxy đang gán cho một nick. Đổi proxy cho nick đó trước.", "proxy_in_use");
        }

        proxy.Status = ProxyStatus.Retired;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Giữ chỗ một proxy trống cho phiên đăng nhập QR (vài phút). null = hết proxy trống. Proxy được gán thật khi quét QR xong.
    /// </summary>
    public async Task<(Guid ProxyId, string ProxyUrl)?> ReserveFreeAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var candidates = await db.Proxies
            .Where(p => p.Status == ProxyStatus.Active && (p.CoolingUntil == null || p.CoolingUntil < now))
            .Where(p => !db.ZaloPersonalAccounts.IgnoreQueryFilters().Any(a => a.ProxyId == p.Id))
            .OrderBy(p => p.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        foreach (var proxy in candidates)
        {
            string url;
            try
            {
                url = encryptor.Decrypt(proxy.CredentialsEnc);
            }
            catch (CryptographicException)
            {
                LogUnreadable(logger, proxy.Id); // đổi khóa mã hóa / dữ liệu hỏng → bỏ qua proxy này, không làm hỏng việc kết nối
                continue;
            }

            if (await store.SetIfNotExistsAsync($"zp:proxy-reserved:{proxy.Id:N}", "1", ReserveTtl, cancellationToken))
            {
                return (proxy.Id, url);
            }
        }

        return null;
    }

    /// <summary>Chỉ trạng thái (không địa chỉ) — để trang Kênh của DN hiện "IP riêng: đang hoạt động / đang lỗi".</summary>
    public async Task<Dictionary<Guid, ProxyStatus>> StatusesAsync(IReadOnlyCollection<Guid> proxyIds, CancellationToken cancellationToken) =>
        proxyIds.Count == 0
            ? []
            : await db.Proxies.AsNoTracking()
                .Where(p => proxyIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Status, cancellationToken);

    /// <summary>URL proxy (đã giải mã) của một proxy còn dùng được; null = không có / hỏng / ngừng dùng.</summary>
    public async Task<string?> UrlIfUsableAsync(Guid? proxyId, CancellationToken cancellationToken)
    {
        if (proxyId is not { } id)
        {
            return null;
        }

        var proxy = await db.Proxies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return proxy is { Status: ProxyStatus.Active } ? encryptor.Decrypt(proxy.CredentialsEnc) : null;
    }

    /// <summary>Đổi proxy của một nick (super admin). Proxy cũ nghỉ vài ngày. Nick chạy lại qua proxy mới.</summary>
    public async Task AssignAsync(Guid connectionId, Guid proxyId, CancellationToken cancellationToken)
    {
        // Super admin sửa nick của DN bất kỳ → cập nhật bằng ExecuteUpdate (thao tác hệ thống), không qua change tracker có kiểm tenant.
        var current = await db.ZaloPersonalAccounts.IgnoreQueryFilters()
            .Where(a => a.ConnectionId == connectionId)
            .Select(a => new { a.ProxyId })
            .FirstOrDefaultAsync(cancellationToken) ?? throw new NotFoundException();
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken) ?? throw new NotFoundException();
        if (proxy.Status != ProxyStatus.Active)
        {
            throw new ConflictException("Proxy đang hỏng hoặc đã ngừng dùng.", "proxy_unusable");
        }

        if (proxy.CoolingUntil is { } cooling && cooling > time.GetUtcNow())
        {
            throw new ConflictException("Proxy vừa thả khỏi một nick, đang nghỉ 7 ngày trước khi gán nick khác.", "proxy_cooling");
        }

        if (await db.ZaloPersonalAccounts.IgnoreQueryFilters().AnyAsync(a => a.ProxyId == proxyId && a.ConnectionId != connectionId, cancellationToken))
        {
            throw new ConflictException("Proxy đang gán cho nick khác.", "proxy_in_use");
        }

        if (current.ProxyId is { } old && old != proxyId)
        {
            await CoolAsync(old, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var now = time.GetUtcNow();
        await db.ZaloPersonalAccounts.IgnoreQueryFilters()
            .Where(a => a.ConnectionId == connectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ProxyId, proxyId), cancellationToken);

        // Nick đang dừng vì proxy cũ hỏng → chạy lại qua proxy mới.
        var revived = await db.ChannelConnections.IgnoreQueryFilters()
            .Where(c => c.Id == connectionId && c.LastError == "zp:proxy_failing")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ConnectionStatus.Active).SetProperty(c => c.LastError, (string?)null), cancellationToken);
        if (revived > 0)
        {
            await db.ZaloPersonalAccounts.IgnoreQueryFilters()
                .Where(a => a.ConnectionId == connectionId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.ListenerState, ListenerState.Starting).SetProperty(a => a.ListenerStateChangedAt, now),
                    cancellationToken);
        }

        LogAssigned(logger, connectionId, proxyId);
    }

    /// <summary>Nick ngắt kết nối / đổi proxy → proxy cũ nghỉ vài ngày rồi mới cho nick khác (tránh nhiều nick lần lượt dùng 1 IP).</summary>
    public async Task CoolAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken);
        if (proxy is not null)
        {
            proxy.CoolingUntil = time.GetUtcNow().AddDays(options.Value.ProxyCooldownDays);
        }
    }

    /// <summary>Ghi kết quả kiểm tra. Trả true nếu trạng thái đổi (hỏng ↔ tốt).</summary>
    internal bool Record(Proxy proxy, SidecarProxyTest result)
    {
        var now = time.GetUtcNow();
        proxy.LastCheckedAt = now;
        var before = proxy.Status;
        if (result.Ok)
        {
            if (proxy.ExitIp is { } previous && result.ExitIp is { } current && previous != current)
            {
                LogExitIpChanged(logger, proxy.Id); // proxy "IP cố định" mà IP ra đổi → super admin kiểm tra nhà cung cấp
            }

            proxy.ExitIp = result.ExitIp ?? proxy.ExitIp;
            proxy.FailCount = 0;
            proxy.FailingSince = null;
            proxy.LastError = null;
            if (proxy.Status == ProxyStatus.Failing)
            {
                proxy.Status = ProxyStatus.Active;
            }
        }
        else
        {
            proxy.FailCount++;
            proxy.LastError = result.Error is { Length: > 200 } e ? e[..200] : result.Error;
            if (proxy.Status == ProxyStatus.Active && proxy.FailCount >= 2)
            {
                proxy.Status = ProxyStatus.Failing;
                proxy.FailingSince = now;
            }
        }

        return before != proxy.Status;
    }

    /// <summary>
    /// http(s)://user:pass@host:port → chuỗi chuẩn hóa (scheme/host chữ thường) + "host:port" để hiện. Sai định dạng → 400.
    /// </summary>
    public static (string Normalized, string Endpoint) Normalize(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
            || uri.IsDefaultPort && !url!.Contains($":{uri.Port}", StringComparison.Ordinal)
            || uri.AbsolutePath is not ("/" or "")
            || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidInputException("Proxy phải có dạng http://tài_khoản:mật_khẩu@máy_chủ:cổng (hoặc https://...).");
        }

        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? "" : uri.UserInfo + "@";
        var endpoint = $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
        return ($"{uri.Scheme}://{userInfo}{endpoint}", endpoint);
    }

    private string Fingerprint(string normalized)
    {
        var master = Convert.FromBase64String(security.Value.EncryptionKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: [], info: Encoding.UTF8.GetBytes("zaloai:proxy-fingerprint:v1"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalized)));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Không giải mã được proxy {ProxyId} — bỏ qua khi cấp proxy")]
    private static partial void LogUnreadable(ILogger logger, Guid proxyId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Đã gán proxy {ProxyId} cho nick {ConnectionId}")]
    private static partial void LogAssigned(ILogger logger, Guid connectionId, Guid proxyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proxy {ProxyId} đổi IP ra ngoài (proxy IP cố định không nên đổi) — kiểm tra nhà cung cấp")]
    private static partial void LogExitIpChanged(ILogger logger, Guid proxyId);
}
