using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Zalo;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Api.Connections;

/// <summary>Giới hạn đang áp hôm nay (sau bậc nick mới, phanh, trần hệ thống).</summary>
/// <param name="NewNickWeek">1 | 2 khi nick mới kết nối (đang ở bậc thấp), null khi đã qua.</param>
public sealed record PacingLimitsResponse(int InvitesPerDay, int NewContactsPerDay, int MessagesPerDay, int MessagesPerHour, int FindUserPerDay, int? NewNickWeek);

/// <summary>Số đã dùng hôm nay (giờ Việt Nam).</summary>
public sealed record PacingUsageResponse(int Invites, int NewContacts, int Messages, int FindUser);

/// <summary>Số liệu 7 ngày của nick mà phanh tự động dựa vào (mục 7.1): lời mời đã gửi / được chấp nhận, tin đầu đã gửi, người từ chối/chặn.</summary>
public sealed record OutreachRatesResponse(int Invites, int Accepted, int FirstMessages, int Negatives);

/// <summary>Phần riêng của nick Zalo cá nhân. Không bao giờ chứa phiên đăng nhập hay địa chỉ proxy.</summary>
/// <param name="ListenerState">starting | connected | reconnecting | openedelsewhere | sessionexpired | proxyfailing | stopped | stale</param>
/// <param name="LastStateCode">Mã đóng kết nối gần nhất (vd 3000 = mở Zalo Web nơi khác).</param>
/// <param name="PacingMode">safe | custom</param>
/// <param name="OutreachLevel">normal | slow | paused (phanh tự động)</param>
/// <param name="Proxy">none | active | failing — chỉ tình trạng, không bao giờ có địa chỉ.</param>
public sealed record PersonalAccountInfo(
    string ListenerState,
    int? LastStateCode,
    DateTimeOffset? ListenerStateChangedAt,
    DateTimeOffset? LastSeenAt,
    bool TreatFriendsAsCustomers,
    int StaffPauseMinutes,
    int PendingPeers,
    string PacingMode = "safe",
    bool AutoBrake = true,
    int? CustomInvitesPerDay = null,
    int? CustomNewContactsPerDay = null,
    int? CustomMessagesPerDay = null,
    PacingLimitsResponse? Limits = null,
    PacingUsageResponse? Usage = null,
    string OutreachLevel = "normal",
    string? OutreachLevelReason = null,
    DateTimeOffset? OutreachPausedUntil = null,
    string Proxy = "none",
    DateTimeOffset? ConnectedAt = null,
    OutreachRatesResponse? Rates7d = null);

/// <summary>Trần hệ thống + mức khuyến cáo (để giao diện chế độ Tùy chỉnh hiện khuyến cáo và chặn vượt trần).</summary>
public sealed record PersonalPacingBounds(
    int MaxInvitesPerDay,
    int MaxNewContactsPerDay,
    int MaxMessagesPerDay,
    int SafeInvitesPerDay,
    int SafeNewContactsPerDay,
    int SafeMessagesPerDay);

/// <param name="Channel">zalo | zalopersonal</param>
/// <param name="ExternalId">Zalo: OA ID. Zalo cá nhân: uid của nick.</param>
/// <param name="Status">active | needsreauth | disconnected</param>
/// <param name="LastError">Mã lỗi gần nhất (ví dụ "zalo:-220", "zp:opened_elsewhere"), không chứa dữ liệu nhạy cảm.</param>
/// <param name="AccessTokenExpiresAt">Chỉ Zalo OA.</param>
/// <param name="RefreshTokenExpiresAt">Chỉ Zalo OA.</param>
/// <param name="Personal">Chỉ Zalo cá nhân.</param>
/// <param name="CustomersDaily">Số khách có tin qua lại trên kênh mỗi ngày, 7 ngày gần nhất (giờ Việt Nam), cũ → mới; phần tử cuối = hôm nay.</param>
public sealed record ChannelConnectionResponse(
    Guid Id,
    string Channel,
    string ExternalId,
    string? Name,
    string Status,
    string? LastError,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt,
    DateTimeOffset CreatedAt,
    PersonalAccountInfo? Personal = null,
    IReadOnlyList<int>? CustomersDaily = null)
{
    internal static ChannelConnectionResponse From(ChannelConnection c, PersonalAccountInfo? personal = null, IReadOnlyList<int>? customersDaily = null) =>
        c.Channel == ChannelKind.ZaloPersonal
            ? new(c.Id, Lower(c.Channel), c.ExternalId, c.Name, Lower(c.Status), c.LastError, null, null, c.CreatedAt, personal, customersDaily)
            : new(c.Id, Lower(c.Channel), c.ExternalId, c.Name, Lower(c.Status), c.LastError, c.AccessTokenExpiresAt, c.RefreshTokenExpiresAt, c.CreatedAt, null, customersDaily);

    internal static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}

/// <param name="Configured">Hệ thống đã cấu hình Zalo App chưa (chưa → ẩn nút kết nối, báo quản trị viên).</param>
/// <param name="PersonalConfigured">Hệ thống đã cấu hình kết nối Zalo cá nhân (sidecar) chưa.</param>
/// <param name="PersonalFakeMode">Sidecar đang chạy giả lập (chỉ dev) → giao diện hiện bảng giả lập.</param>
/// <param name="PersonalMaxAccounts">Số nick Zalo cá nhân tối đa mỗi DN.</param>
public sealed record ChannelsResponse(
    bool Configured,
    IReadOnlyList<ChannelConnectionResponse> Connections,
    bool PersonalConfigured = false,
    bool PersonalFakeMode = false,
    int PersonalMaxAccounts = 0,
    PersonalPacingBounds? PersonalPacing = null);

/// <param name="Url">Trang cấp quyền của Zalo — trình duyệt chuyển hướng tới đây.</param>
public sealed record ConnectUrlResponse(string Url);

/// <summary>
/// Kênh đã kết nối (GĐ1: Zalo OA). tenantId luôn từ ITenantContext; callback của Zalo không đăng nhập được nên tenant lấy từ
/// OAuth state (Redis, dùng 1 lần) — không bao giờ từ query. Token không bao giờ trả ra API. URL callback chứa code: không log.
/// </summary>
internal static partial class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/channels").WithTags("Channels");

        group.MapGet("", ListAsync).RequireTenantRole(TenantRole.Staff);

        group.MapPost("/zalo/connect", ConnectAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/{connectionId:guid}", DisconnectAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Zalo chuyển hướng trình duyệt của admin OA về đây (GET ?code=&oa_id=&state=). Không yêu cầu đăng nhập.
        app.MapGet("/connect/zalo/callback", CallbackAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitSettings.OAuthCallbackPolicy)
            .ExcludeFromDescription();

        return app;
    }

    private static async Task<Ok<ChannelsResponse>> ListAsync(
        ITenantContext tenant,
        ChannelConnectionRepository connections,
        ZaloPersonalRepository personal,
        PersonalSendLimiter limiter,
        OutreachRepository outreach,
        ConversationRepository conversations,
        ProxyPoolService proxies,
        TimeProvider time,
        IOptions<ZaloOptions> zalo,
        IOptions<ZaloPersonalOptions> zaloPersonal,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var p = zaloPersonal.Value;
        var now = time.GetUtcNow();
        var list = await connections.ListAsync(tenantId, cancellationToken);
        var accounts = (await personal.ListAccountsAsync(tenantId, cancellationToken)).ToDictionary(a => a.ConnectionId);
        var pending = accounts.Count == 0 ? [] : await personal.CountPendingPeersAsync(tenantId, cancellationToken);
        var proxyStatus = await proxies.StatusesAsync(accounts.Values.Where(a => a.ProxyId is not null).Select(a => a.ProxyId!.Value).ToList(), cancellationToken);

        var items = new List<ChannelConnectionResponse>(list.Count);
        foreach (var c in list)
        {
            var customersDaily = await conversations.DailyActiveConversationsAsync(tenantId, c.Id, now, 7, cancellationToken);
            if (!accounts.TryGetValue(c.Id, out var a))
            {
                items.Add(ChannelConnectionResponse.From(c, customersDaily: customersDaily));
                continue;
            }

            var limits = PersonalPacing.Effective(a, p, now);
            var usage = await limiter.UsageAsync(c.Id, cancellationToken);
            var (invites, accepted, firstMessages, negatives) = await outreach.RatesAsync(tenantId, c.Id, now.AddDays(-7), cancellationToken);
            var proxy = a.ProxyId is { } proxyId
                ? proxyStatus.GetValueOrDefault(proxyId) == ProxyStatus.Active ? "active" : "failing"
                : "none";
            items.Add(ChannelConnectionResponse.From(c, new PersonalAccountInfo(
                ChannelConnectionResponse.Lower(a.ListenerState),
                a.LastStateCode,
                a.ListenerStateChangedAt,
                a.LastSeenAt,
                a.TreatFriendsAsCustomers,
                a.StaffPauseMinutes,
                pending.GetValueOrDefault(c.Id),
                ChannelConnectionResponse.Lower(a.PacingMode),
                a.AutoBrake,
                a.CustomInvitesPerDay,
                a.CustomNewContactsPerDay,
                a.CustomMessagesPerDay,
                new PacingLimitsResponse(
                    limits.InvitesPerDay, limits.NewContactsPerDay, limits.MessagesPerDay, limits.MessagesPerHour, limits.FindUserPerDay, limits.NewNickWeek),
                new PacingUsageResponse(usage.Invites, usage.NewContacts, usage.Messages, usage.FindUser),
                ChannelConnectionResponse.Lower(a.OutreachLevel),
                a.OutreachLevelReason,
                a.OutreachPausedUntil,
                proxy,
                a.CreatedAt,
                new OutreachRatesResponse(invites, accepted, firstMessages, negatives)),
                customersDaily));
        }

        return TypedResults.Ok(new ChannelsResponse(
            zalo.Value.IsConfigured,
            items,
            p.IsConfigured,
            p.IsConfigured && p.Mode == ZaloPersonalMode.Fake,
            p.MaxAccountsPerTenant,
            new PersonalPacingBounds(
                p.MaxInvitesPerDay,
                p.MaxNewContactsPerDay,
                p.MaxMessagesPerDay,
                PersonalPacing.SafeInvitesPerDay,
                PersonalPacing.SafeNewContactsPerDay,
                PersonalPacing.SafeMessagesPerDay)));
    }

    private static async Task<Ok<ConnectUrlResponse>> ConnectAsync(
        ITenantContext tenant,
        ZaloConnectionService service,
        CancellationToken cancellationToken)
    {
        var url = await service.StartAsync(tenant.RequireTenantId(), tenant.UserId ?? throw new ForbiddenException(), cancellationToken);
        return TypedResults.Ok(new ConnectUrlResponse(url));
    }

    private static async Task<NoContent> DisconnectAsync(
        Guid connectionId,
        ITenantContext tenant,
        ChannelConnectionRepository connections,
        AuditLogRepository audit,
        ZaloPersonalConnectionService personal,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken) ?? throw new NotFoundException();
        if (connection.Channel == ChannelKind.ZaloPersonal)
        {
            await personal.ReleaseProxyAsync(tenantId, connectionId, cancellationToken); // proxy nghỉ vài ngày rồi mới cấp nick khác
        }

        connections.Remove(tenantId, connection); // xóa luôn token / phiên Zalo cá nhân (cascade); hội thoại cũ giữ lại
        audit.Add(tenantId, tenant.UserId, "channel.disconnected", $"{connection.Channel.ToString().ToLowerInvariant()}:{connection.ExternalId}");
        await connections.SaveChangesAsync(cancellationToken);
        if (connection.Channel == ChannelKind.ZaloPersonal)
        {
            await personal.StopAfterDisconnectAsync(connectionId, cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>Hoàn tất kết nối rồi đưa admin về trang Kênh của FE kèm kết quả (không kèm dữ liệu nhạy cảm).</summary>
    private static async Task<RedirectHttpResult> CallbackAsync(
        string? code,
        string? state,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "oa_id")] string? oaId,
        ZaloConnectionService service,
        IOptions<AppOptions> app,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var target = $"{app.Value.AdminUrl.TrimEnd('/')}/channels";
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(oaId)
            || oaId.Length > 50 || state.Length > 100 || code.Length > 2000)
        {
            return TypedResults.Redirect($"{target}?error=invalid_callback");
        }

        try
        {
            await service.CompleteAsync(state, code, oaId, cancellationToken);
            return TypedResults.Redirect($"{target}?connected=zalo");
        }
        catch (Exception ex) when (ex is InvalidInputException or ConflictException or ZaloApiException or ZaloTransientException or ZaloNotConfiguredException)
        {
            var reason = ex switch
            {
                ConflictException => "oa_in_use",
                InvalidInputException => "expired",
                ZaloApiException z => z.ShortCode.Replace(':', '_'),
                _ => "zalo_unavailable",
            };
            // ZaloApiException.Message = mã + câu mô tả lỗi cố định của Zalo (không có token/code) → ghi để chẩn đoán.
            LogCallbackFailed(loggerFactory.CreateLogger("ZaloAi.Api.Connections"), reason, ex is ZaloApiException ? ex.Message : ex.GetType().Name);
            return TypedResults.Redirect($"{target}?error={Uri.EscapeDataString(reason)}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kết nối Zalo không hoàn tất: {Reason} ({Detail})")]
    private static partial void LogCallbackFailed(ILogger logger, string reason, string detail);
}
