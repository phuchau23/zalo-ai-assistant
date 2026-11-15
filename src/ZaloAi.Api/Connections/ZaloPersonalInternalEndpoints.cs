using System.Text.Json;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Api.Connections;

/// <summary>
/// Sidecar Zalo cá nhân gọi vào (connectors/zalo-personal/CONTRACT.md mục 4). Hai lớp chặn: chỉ nghe ở cổng nội bộ riêng
/// (ZaloPersonal:InternalPort, không đi qua tunnel / reverse proxy công khai) VÀ bắt buộc chữ ký HMAC đúng, còn hạn 5 phút.
/// Đọc nguyên body để kiểm chữ ký trên đúng chuỗi sidecar gửi. Không log body (có phiên đăng nhập, nội dung tin).
/// </summary>
internal static class ZaloPersonalInternalEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int MaxEventsPerBatch = 500;

    private const int MaxQrLength = 200_000;

    public static IEndpointRouteBuilder MapZaloPersonalInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var port = app.ServiceProvider.GetRequiredService<IOptions<ZaloPersonalOptions>>().Value.InternalPort;
        var group = app.MapGroup("/internal/zalo-personal")
            .RequireHost($"*:{port}")
            .AllowAnonymous()
            .ExcludeFromDescription();

        group.MapPost("/sync", SyncAsync);
        group.MapPost("/login-progress", LoginProgressAsync);
        group.MapPost("/login-result", LoginResultAsync);
        group.MapPost("/events", EventsAsync);
        group.MapPost("/session", SessionAsync);
        return app;
    }

    private sealed record SyncRequest(string Instance, List<SidecarRunning>? Running);

    private sealed record LoginProgressRequest(string LoginId, string State, string? QrPng, string? Error);

    private sealed record LoginResultRequest(string LoginId, string Uid, string? DisplayName, string? AvatarUrl, JsonElement Session);

    private sealed record EventsRequest(Guid ConnectionId, List<PersonalEvent>? Events);

    private sealed record SessionRequest(Guid ConnectionId, JsonElement Session);

    private static async Task<IResult> SyncAsync(HttpRequest request, ZaloPersonalSyncService sync, CancellationToken cancellationToken)
    {
        var body = await ReadSignedAsync<SyncRequest>(request, cancellationToken);
        if (body is null)
        {
            return Results.Unauthorized();
        }

        var result = await sync.SyncAsync(body.Instance, body.Running ?? [], cancellationToken);
        return Results.Json(result, Json);
    }

    private static async Task<IResult> LoginProgressAsync(HttpRequest request, ZaloPersonalLoginService login, CancellationToken cancellationToken)
    {
        var body = await ReadSignedAsync<LoginProgressRequest>(request, cancellationToken);
        if (body is null)
        {
            return Results.Unauthorized();
        }

        if (body.QrPng is { Length: > MaxQrLength })
        {
            return Results.BadRequest();
        }

        return await login.OnProgressAsync(body.LoginId, body.State, body.QrPng, body.Error, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> LoginResultAsync(HttpRequest request, ZaloPersonalLoginService login, CancellationToken cancellationToken)
    {
        var body = await ReadSignedAsync<LoginResultRequest>(request, cancellationToken);
        if (body is null)
        {
            return Results.Unauthorized();
        }

        var outcome = await login.OnResultAsync(body.LoginId, body.Uid, body.DisplayName, body.Session, cancellationToken);
        return outcome.ConnectionId is { } id
            ? Results.Json(new { connectionId = id }, Json)
            : Results.Json(new { error = outcome.Error }, Json, statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> EventsAsync(HttpRequest request, ZaloPersonalIngestor ingestor, CancellationToken cancellationToken)
    {
        var body = await ReadSignedAsync<EventsRequest>(request, cancellationToken);
        if (body is null)
        {
            return Results.Unauthorized();
        }

        if (body.Events is not { Count: > 0 and <= MaxEventsPerBatch })
        {
            return Results.BadRequest();
        }

        return await ingestor.HandleAsync(body.ConnectionId, body.Events, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SessionAsync(HttpRequest request, ZaloPersonalIngestor ingestor, CancellationToken cancellationToken)
    {
        var body = await ReadSignedAsync<SessionRequest>(request, cancellationToken);
        if (body is null)
        {
            return Results.Unauthorized();
        }

        return await ingestor.ReplaceSessionAsync(body.ConnectionId, body.Session, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>null = chữ ký sai / hết hạn / chưa cấu hình / body không đọc được → 401 (không nói lý do).</summary>
    private static async Task<T?> ReadSignedAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        var options = request.HttpContext.RequestServices.GetRequiredService<IOptions<ZaloPersonalOptions>>().Value;
        var time = request.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        if (string.IsNullOrEmpty(options.SharedSecret))
        {
            return null;
        }

        using var reader = new StreamReader(request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);
        var pathAndQuery = $"{request.PathBase}{request.Path}{request.QueryString}";
        if (!RequestSignature.IsValid(
                request.Headers[RequestSignature.HeaderName], options.SharedSecret, time.GetUtcNow(), request.Method, pathAndQuery, raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
