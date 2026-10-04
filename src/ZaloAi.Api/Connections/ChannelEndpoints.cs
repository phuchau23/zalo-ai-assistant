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

namespace ZaloAi.Api.Connections;

/// <param name="Channel">zalo</param>
/// <param name="ExternalId">Zalo: OA ID.</param>
/// <param name="Status">active | needsreauth</param>
/// <param name="LastError">Mã lỗi gần nhất (ví dụ "zalo:-220"), không chứa dữ liệu nhạy cảm.</param>
public sealed record ChannelConnectionResponse(
    Guid Id,
    string Channel,
    string ExternalId,
    string? Name,
    string Status,
    string? LastError,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt,
    DateTimeOffset CreatedAt)
{
    internal static ChannelConnectionResponse From(ChannelConnection c) =>
        new(c.Id, Lower(c.Channel), c.ExternalId, c.Name, Lower(c.Status), c.LastError, c.AccessTokenExpiresAt, c.RefreshTokenExpiresAt, c.CreatedAt);

    private static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}

/// <param name="Configured">Hệ thống đã cấu hình Zalo App chưa (chưa → ẩn nút kết nối, báo quản trị viên).</param>
public sealed record ChannelsResponse(bool Configured, IReadOnlyList<ChannelConnectionResponse> Connections);

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
        IOptions<ZaloOptions> zalo,
        CancellationToken cancellationToken)
    {
        var list = await connections.ListAsync(tenant.RequireTenantId(), cancellationToken);
        return TypedResults.Ok(new ChannelsResponse(zalo.Value.IsConfigured, list.Select(ChannelConnectionResponse.From).ToList()));
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
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var connection = await connections.GetAsync(tenantId, connectionId, cancellationToken) ?? throw new NotFoundException();
        connections.Remove(tenantId, connection); // xóa luôn token; hội thoại cũ giữ lại
        audit.Add(tenantId, tenant.UserId, "channel.disconnected", $"{connection.Channel.ToString().ToLowerInvariant()}:{connection.ExternalId}");
        await connections.SaveChangesAsync(cancellationToken);
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
            LogCallbackFailed(loggerFactory.CreateLogger("ZaloAi.Api.Connections"), reason);
            return TypedResults.Redirect($"{target}?error={Uri.EscapeDataString(reason)}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kết nối Zalo không hoàn tất: {Reason}")]
    private static partial void LogCallbackFailed(ILogger logger, string reason);
}
