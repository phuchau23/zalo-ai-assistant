using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;

namespace ZaloAi.Api.Connections;

/// <summary>
/// File xác thực tên miền của Zalo (trang app → Xác thực domain → Tiền tố URL → "Tải tệp HTML lên trang web").
/// Zalo bắt Callback URL nằm trên tên miền đã xác thực; mỗi tên miền con (dev-api, api) xác thực riêng.
/// Nội dung y hệt file Zalo cho tải về (đã đối chiếu 2026-11-11). Chỉ map khi có Zalo:SiteVerificationToken.
/// </summary>
internal static class ZaloSiteVerificationEndpoints
{
    public static IEndpointRouteBuilder MapZaloSiteVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var token = app.ServiceProvider.GetRequiredService<IOptions<ZaloOptions>>().Value.SiteVerificationToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return app;
        }

        var html = $"""
            <!DOCTYPE html>
            <html lang="en">

            <head>
                <meta property="zalo-platform-site-verification" content="{token}" />
            </head>

            <body>
            There Is No Limit To What You Can Accomplish Using Zalo!
            </body>

            </html>
            """;

        app.MapGet($"/zalo_verifier{token}.html", () => Results.Content(html, "text/html; charset=utf-8"))
            .AllowAnonymous()
            .ExcludeFromDescription();
        return app;
    }
}
