using Microsoft.AspNetCore.Mvc;
using ZaloAi.Api.Common;
using ZaloAi.Infrastructure.Zalo;

namespace ZaloAi.Api.Connections;

/// <summary>
/// Zalo gọi vào đây cho mọi OA đã cấp quyền cho app (một URL cho cả app). Đọc nguyên body để kiểm chữ ký trên đúng chuỗi Zalo gửi.
/// Sai chữ ký → 401; còn lại luôn 200 nhanh (Zalo chỉ chờ 2 giây, quá hạn nhiều lần sẽ tự hủy đăng ký webhook).
/// </summary>
internal static class ZaloWebhookEndpoints
{
    private const int MaxBodyBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapZaloWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/zalo", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitSettings.WebhookPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> HandleAsync(HttpRequest request, ZaloWebhookIngestor ingestor, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var outcome = await ingestor.HandleAsync(body, request.Headers["X-ZEvent-Signature"].FirstOrDefault(), cancellationToken);
        return outcome == WebhookOutcome.Rejected ? Results.Unauthorized() : Results.Ok();
    }
}
