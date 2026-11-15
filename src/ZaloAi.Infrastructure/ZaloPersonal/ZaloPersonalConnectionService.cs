using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Quản lý nick Zalo cá nhân đã kết nối: kết nối lại, cài đặt, phân loại người quen, dừng khi ngắt kết nối.</summary>
public sealed partial class ZaloPersonalConnectionService(
    ChannelConnectionRepository connections,
    ZaloPersonalRepository personal,
    AuditLogRepository audit,
    SidecarClient sidecar,
    ProxyPoolService proxies,
    IFieldEncryptor encryptor,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time,
    ILogger<ZaloPersonalConnectionService> logger)
{
    /// <summary>
    /// Bấm "Kết nối lại" sau khi mất kết nối (vd đã đóng Zalo Web). Phiên hết hạn thì không kết nối lại được, phải quét QR
    /// (<see cref="ConflictException"/> mã zalo_personal_needs_qr). Proxy của nick hỏng / thiếu proxy khi bắt buộc → không chạy.
    /// </summary>
    public async Task ReconnectAsync(Guid tenantId, Guid? userId, Guid connectionId, CancellationToken cancellationToken)
    {
        var (connection, account) = await GetAsync(tenantId, connectionId, cancellationToken);
        if (connection.Status == ConnectionStatus.NeedsReauth)
        {
            throw new ConflictException("Phiên đăng nhập đã hết hạn. Quét lại mã QR để kết nối.", "zalo_personal_needs_qr");
        }

        var proxyUrl = await proxies.UrlIfUsableAsync(account.ProxyId, cancellationToken);
        if ((account.ProxyId is not null && proxyUrl is null) || (options.Value.RequireProxy && account.ProxyId is null))
        {
            throw new ConflictException("Đường kết nối riêng (IP) của nick đang lỗi hoặc chưa có. Remi đang xử lý, bạn thử lại sau.", "zalo_personal_proxy_unusable");
        }

        var now = time.GetUtcNow();
        connection.Status = ConnectionStatus.Active;
        connection.LastError = null;
        account.ListenerState = ListenerState.Starting;
        account.ListenerStateChangedAt = now;
        account.LastStateCode = null;
        audit.Add(tenantId, userId, "channel.reconnected", $"zalopersonal:{connection.ExternalId}");
        await connections.SaveChangesAsync(cancellationToken);

        // Chạy ngay thay vì chờ lượt đồng bộ kế tiếp (≤ 60 giây). Sidecar chưa sẵn sàng thì lượt đồng bộ sau sẽ chạy.
        try
        {
            using var session = JsonDocument.Parse(encryptor.Decrypt(account.SessionEnc));
            await sidecar.StartAccountAsync(connectionId, session.RootElement, proxyUrl, cancellationToken);
        }
        catch (ChannelUnavailableException)
        {
            LogSidecarDeferred(logger, connectionId);
        }
    }

    public async Task<ZaloPersonalAccount> UpdateSettingsAsync(
        Guid tenantId, Guid? userId, Guid connectionId, bool treatFriendsAsCustomers, int staffPauseMinutes, CancellationToken cancellationToken)
    {
        if (staffPauseMinutes is < 0 or > 1440)
        {
            throw new InvalidInputException("Số phút tạm dừng bot phải từ 0 đến 1440.");
        }

        var (connection, account) = await GetAsync(tenantId, connectionId, cancellationToken);
        account.TreatFriendsAsCustomers = treatFriendsAsCustomers;
        account.StaffPauseMinutes = staffPauseMinutes;
        audit.Add(tenantId, userId, "channel.settings_changed", $"zalopersonal:{connection.ExternalId}");
        await connections.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task<IReadOnlyList<ZaloPeer>> ListPeersAsync(Guid tenantId, Guid connectionId, PeerKind? kind, CancellationToken cancellationToken)
    {
        await GetAsync(tenantId, connectionId, cancellationToken);
        return await personal.ListPeersAsync(tenantId, connectionId, kind, cancellationToken);
    }

    /// <summary>Chủ nick đánh dấu một người nhắn là khách (bot xử lý từ tin sau) hoặc riêng tư (bỏ qua hẳn).</summary>
    public async Task<ZaloPeer> DecidePeerAsync(Guid tenantId, Guid? userId, Guid peerId, PeerKind kind, CancellationToken cancellationToken)
    {
        if (kind == PeerKind.Pending)
        {
            throw new InvalidInputException("Chỉ chọn được \"khách\" hoặc \"riêng tư\".");
        }

        var peer = await personal.GetPeerByIdAsync(tenantId, peerId, cancellationToken) ?? throw new NotFoundException();
        peer.Kind = kind;
        peer.DecidedByUserId = userId;
        peer.DecidedAt = time.GetUtcNow();
        audit.Add(tenantId, userId, kind == PeerKind.Customer ? "zalo_peer.marked_customer" : "zalo_peer.marked_private", peer.Id.ToString());
        await personal.SaveChangesAsync(cancellationToken);
        return peer;
    }

    /// <summary>
    /// Đổi nhịp gửi (docs/ZALO-PERSONAL-PLAN.md mục 7.2). An toàn: hệ thống tự đặt số, phanh luôn bật. Tùy chỉnh: số do chủ DN nhập
    /// (0..trần hệ thống, vượt → 400); chuyển sang Tùy chỉnh hoặc tắt phanh phải xác nhận rủi ro. Ghi audit số cũ → số mới.
    /// </summary>
    public async Task<ZaloPersonalAccount> UpdatePacingAsync(
        Guid tenantId,
        Guid? userId,
        Guid connectionId,
        PacingMode mode,
        int? invitesPerDay,
        int? newContactsPerDay,
        int? messagesPerDay,
        bool autoBrake,
        bool confirmRisk,
        CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (mode == PacingMode.Custom)
        {
            Check(invitesPerDay, o.MaxInvitesPerDay, "Lời mời kết bạn mỗi ngày");
            Check(newContactsPerDay, o.MaxNewContactsPerDay, "Người mới nhắn lần đầu mỗi ngày");
            Check(messagesPerDay, o.MaxMessagesPerDay, "Tổng tin mỗi ngày");
        }

        var (connection, account) = await GetAsync(tenantId, connectionId, cancellationToken);
        var riskier = (mode == PacingMode.Custom && account.PacingMode == PacingMode.Safe) || (mode == PacingMode.Custom && !autoBrake && account.AutoBrake);
        if (riskier && !confirmRisk)
        {
            throw new InvalidInputException("Cần xác nhận đã hiểu rủi ro nick bị Zalo hạn chế hoặc khóa trước khi đổi.");
        }

        var before = Describe(account);
        account.PacingMode = mode;
        if (mode == PacingMode.Custom)
        {
            account.CustomInvitesPerDay = invitesPerDay;
            account.CustomNewContactsPerDay = newContactsPerDay;
            account.CustomMessagesPerDay = messagesPerDay;
            account.AutoBrake = autoBrake;
        }
        else
        {
            account.AutoBrake = true; // an toàn: phanh luôn bật (số tùy chỉnh cũ giữ lại cho lần chuyển sau)
        }

        audit.Add(tenantId, userId, "channel.pacing_changed", $"zalopersonal:{connection.ExternalId} {before} → {Describe(account)}");
        await connections.SaveChangesAsync(cancellationToken);
        return account;

        static void Check(int? value, int max, string name)
        {
            if (value is { } v && (v < 0 || v > max))
            {
                throw new InvalidInputException($"{name} phải từ 0 đến {max} (trần hệ thống).");
            }
        }

        static string Describe(ZaloPersonalAccount a) =>
            a.PacingMode == PacingMode.Safe
                ? "safe"
                : $"custom(invites={a.CustomInvitesPerDay?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"},new={a.CustomNewContactsPerDay?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"},msgs={a.CustomMessagesPerDay?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"},brake={a.AutoBrake})";
    }

    /// <summary>Trước khi xóa kết nối: proxy của nick nghỉ vài ngày rồi mới cấp cho nick khác (lưu cùng lần SaveChanges xóa kết nối).</summary>
    public async Task ReleaseProxyAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        if (await personal.GetAccountAsync(tenantId, connectionId, cancellationToken) is { ProxyId: { } proxyId })
        {
            await proxies.CoolAsync(proxyId, cancellationToken);
        }
    }

    /// <summary>Sau khi xóa kết nối: dừng nick ở sidecar ngay (không được thì lượt đồng bộ sau tự dừng, vì nick không còn trong danh sách).</summary>
    public async Task StopAfterDisconnectAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        try
        {
            await sidecar.StopAccountAsync(connectionId, cancellationToken);
        }
        catch (ChannelUnavailableException)
        {
            LogSidecarDeferred(logger, connectionId);
        }
    }

    private async Task<(ChannelConnection Connection, ZaloPersonalAccount Account)> GetAsync(
        Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken);
        var account = connection is { Channel: ChannelKind.ZaloPersonal }
            ? await personal.GetAccountAsync(tenantId, connectionId, cancellationToken)
            : null;
        return connection is null || account is null ? throw new NotFoundException() : (connection, account);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chưa gọi được sidecar cho nick {ConnectionId}; lượt đồng bộ kế tiếp sẽ xử lý")]
    private static partial void LogSidecarDeferred(ILogger logger, Guid connectionId);
}
