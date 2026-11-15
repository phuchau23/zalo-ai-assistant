using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Nick Zalo cá nhân + người đã nhắn vào nick (lọc chuyện riêng). Phiên đăng nhập mã hóa ở tầng gọi.</summary>
public sealed class ZaloPersonalRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<ZaloPersonalAccount?> GetAccountAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ZaloPersonalAccounts
            .Where(a => a.TenantId == tenantId && a.ConnectionId == connectionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ZaloPersonalAccount>> ListAccountsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.ZaloPersonalAccounts.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(cancellationToken);
    }

    public Task<int> CountAccountsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ChannelConnections.CountAsync(c => c.TenantId == tenantId && c.Channel == ChannelKind.ZaloPersonal, cancellationToken);
    }

    public ZaloPersonalAccount AddAccount(Guid tenantId, ZaloPersonalAccount account)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(account);
        account.TenantId = tenantId;
        Db.ZaloPersonalAccounts.Add(account);
        return account;
    }

    /// <summary>Được track.</summary>
    public Task<ZaloPeer?> GetPeerAsync(Guid tenantId, Guid connectionId, string externalUserId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ZaloPeers
            .Where(p => p.TenantId == tenantId && p.ConnectionId == connectionId && p.ExternalUserId == externalUserId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<ZaloPeer?> GetPeerByIdAsync(Guid tenantId, Guid peerId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ZaloPeers.Where(p => p.TenantId == tenantId && p.Id == peerId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ZaloPeer>> ListPeersAsync(Guid tenantId, Guid connectionId, PeerKind? kind, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.ZaloPeers.AsNoTracking().Where(p => p.TenantId == tenantId && p.ConnectionId == connectionId);
        if (kind is { } k)
        {
            query = query.Where(p => p.Kind == k);
        }

        return await query.OrderByDescending(p => p.LastSeenAt).Take(500).ToListAsync(cancellationToken);
    }

    /// <summary>Số người quen chưa phân loại theo từng nick (hiện huy hiệu trên trang Kênh).</summary>
    public async Task<Dictionary<Guid, int>> CountPendingPeersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.ZaloPeers.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Kind == PeerKind.Pending)
            .GroupBy(p => p.ConnectionId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    }

    public ZaloPeer AddPeer(Guid tenantId, ZaloPeer peer)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.TenantId != Guid.Empty && peer.TenantId != tenantId)
        {
            throw new TenantIsolationException("Người nhắn không thuộc tenant hiện tại.");
        }

        peer.TenantId = tenantId;
        if (peer.Id == Guid.Empty)
        {
            peer.Id = Guid.CreateVersion7();
        }

        Db.ZaloPeers.Add(peer);
        return peer;
    }
}

/// <param name="TenantActive">Tenant còn hoạt động (bị khóa/hết hạn → bỏ qua tin, không trả lời).</param>
public sealed record PersonalRoute(Guid ConnectionId, Guid TenantId, ConnectionStatus Status, bool TenantActive);

/// <param name="SessionEnc">Phiên đã mã hóa — chỉ giải mã ngay trước khi giao cho sidecar.</param>
/// <param name="ProxyEnc">URL proxy đã mã hóa nếu nick có proxy CÒN DÙNG ĐƯỢC.</param>
/// <param name="HasProxy">Nick có gán proxy (kể cả proxy đang hỏng) — có proxy mà ProxyEnc null = proxy hỏng → không chạy nick.</param>
public sealed record PersonalRunTarget(Guid ConnectionId, Guid TenantId, string SessionEnc, bool HasProxy = false, string? ProxyEnc = null);

/// <summary>
/// Truy vấn HỆ THỐNG cho sidecar Zalo cá nhân, chạy trước khi biết tenant: sidecar chỉ biết connectionId; đồng bộ nick cần chạy và
/// nhịp tim là việc của mọi tenant. IgnoreQueryFilters() CÓ CHỦ ĐÍCH. Đọc/ghi dữ liệu nghiệp vụ tiếp theo phải set tenant context rồi
/// dùng <see cref="ZaloPersonalRepository"/>.
/// </summary>
public sealed class ZaloPersonalLookup(AppDbContext db)
{
    public Task<PersonalRoute?> FindRouteAsync(Guid connectionId, CancellationToken cancellationToken) =>
        (from c in db.ChannelConnections.IgnoreQueryFilters()
         join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
         where c.Id == connectionId && c.Channel == ChannelKind.ZaloPersonal
         select new PersonalRoute(c.Id, c.TenantId, c.Status, t.Status == TenantStatus.Active))
        .AsNoTracking()
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Nick sidecar phải chạy: kết nối còn hoạt động, tenant còn hoạt động.</summary>
    public async Task<IReadOnlyList<PersonalRunTarget>> ListRunTargetsAsync(CancellationToken cancellationToken) =>
        await (from a in db.ZaloPersonalAccounts.IgnoreQueryFilters()
               join c in db.ChannelConnections.IgnoreQueryFilters() on a.ConnectionId equals c.Id
               join t in db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
               join p in db.Proxies on a.ProxyId equals p.Id into proxies
               from p in proxies.DefaultIfEmpty()
               where c.Channel == ChannelKind.ZaloPersonal && c.Status == ConnectionStatus.Active && t.Status == TenantStatus.Active
               select new PersonalRunTarget(
                   a.ConnectionId,
                   a.TenantId,
                   a.SessionEnc,
                   a.ProxyId != null,
                   p != null && p.Status == ProxyStatus.Active ? p.CredentialsEnc : null))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    /// <summary>Proxy hỏng → dừng mọi nick đang chạy qua nó (fail-closed). Trả các nick vừa dừng để báo chủ DN.</summary>
    public async Task<IReadOnlyList<(Guid ConnectionId, Guid TenantId)>> StopForProxyAsync(Guid proxyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await (from a in db.ZaloPersonalAccounts.IgnoreQueryFilters()
                          join c in db.ChannelConnections.IgnoreQueryFilters() on a.ConnectionId equals c.Id
                          where a.ProxyId == proxyId && c.Status == ConnectionStatus.Active
                          select new { c.Id, c.TenantId })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        await db.ChannelConnections.IgnoreQueryFilters()
            .Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ConnectionStatus.Disconnected).SetProperty(c => c.LastError, "zp:proxy_failing"), cancellationToken);
        await db.ZaloPersonalAccounts.IgnoreQueryFilters()
            .Where(a => ids.Contains(a.ConnectionId))
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.ListenerState, ListenerState.ProxyFailing).SetProperty(a => a.ListenerStateChangedAt, now),
                cancellationToken);
        return rows.Select(r => (r.Id, r.TenantId)).ToList();
    }

    /// <summary>Proxy khỏe lại → nick đã dừng vì nó chạy lại (không bắt chủ DN làm gì).</summary>
    public async Task ResumeForProxyAsync(Guid proxyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = await db.ZaloPersonalAccounts.IgnoreQueryFilters()
            .Where(a => a.ProxyId == proxyId)
            .Select(a => a.ConnectionId)
            .ToListAsync(cancellationToken);
        var revived = await db.ChannelConnections.IgnoreQueryFilters()
            .Where(c => ids.Contains(c.Id) && c.LastError == "zp:proxy_failing")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ConnectionStatus.Active).SetProperty(c => c.LastError, (string?)null), cancellationToken);
        if (revived > 0)
        {
            await db.ZaloPersonalAccounts.IgnoreQueryFilters()
                .Where(a => ids.Contains(a.ConnectionId) && a.ListenerState == ListenerState.ProxyFailing)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.ListenerState, ListenerState.Starting).SetProperty(a => a.ListenerStateChangedAt, now),
                    cancellationToken);
        }
    }

    public Task<int> CountFreeProxiesAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Proxies.CountAsync(
            p => p.Status == ProxyStatus.Active
                 && (p.CoolingUntil == null || p.CoolingUntil < now)
                 && !db.ZaloPersonalAccounts.IgnoreQueryFilters().Any(a => a.ProxyId == p.Id),
            cancellationToken);

    /// <summary>Nhịp tim: sidecar báo các nick đang chạy. Không đổi gì khác.</summary>
    public Task<int> TouchSeenAsync(IReadOnlyCollection<Guid> connectionIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        connectionIds.Count == 0
            ? Task.FromResult(0)
            : db.ZaloPersonalAccounts.IgnoreQueryFilters()
                .Where(a => connectionIds.Contains(a.ConnectionId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, now), cancellationToken);

    /// <summary>
    /// Sidecar báo nick đang kết nối mà DB còn ghi "mất liên lạc"/"đang khởi động" (vd API vừa tắt một lúc, sidecar vẫn chạy và không
    /// phát lại sự kiện connected) → đưa về connected.
    /// </summary>
    public Task<int> RecoverConnectedAsync(IReadOnlyCollection<Guid> connectionIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        connectionIds.Count == 0
            ? Task.FromResult(0)
            : db.ZaloPersonalAccounts.IgnoreQueryFilters()
                .Where(a => connectionIds.Contains(a.ConnectionId)
                            && (a.ListenerState == ListenerState.Stale || a.ListenerState == ListenerState.Starting))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.ListenerState, ListenerState.Connected).SetProperty(a => a.ListenerStateChangedAt, now),
                    cancellationToken);

    /// <summary>Đánh dấu "mất liên lạc với máy chủ kết nối" (job hệ thống, nhiều tenant một lần → không qua tenant context).</summary>
    public Task<int> MarkStaleAsync(IReadOnlyCollection<Guid> connectionIds, DateTimeOffset now, CancellationToken cancellationToken) =>
        connectionIds.Count == 0
            ? Task.FromResult(0)
            : db.ZaloPersonalAccounts.IgnoreQueryFilters()
                .Where(a => connectionIds.Contains(a.ConnectionId))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(a => a.ListenerState, ListenerState.Stale).SetProperty(a => a.ListenerStateChangedAt, now),
                    cancellationToken);

    /// <summary>Nick đáng lẽ đang chạy mà quá hạn không thấy nhịp tim, chưa bị đánh dấu.</summary>
    public async Task<IReadOnlyList<(Guid ConnectionId, Guid TenantId)>> ListStaleAsync(DateTimeOffset seenBefore, CancellationToken cancellationToken)
    {
        var rows = await (from a in db.ZaloPersonalAccounts.IgnoreQueryFilters()
                          join c in db.ChannelConnections.IgnoreQueryFilters() on a.ConnectionId equals c.Id
                          where c.Status == ConnectionStatus.Active
                                && a.ListenerState != ListenerState.Stale
                                && (a.LastSeenAt ?? a.CreatedAt) < seenBefore
                          select new { a.ConnectionId, a.TenantId })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.ConnectionId, r.TenantId)).ToList();
    }
}
