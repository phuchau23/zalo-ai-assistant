using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Api.Tenants;

public sealed record IndustryResponse(string Slug, string Name);

internal static class IndustryEndpoints
{
    public static IEndpointRouteBuilder MapIndustryEndpoints(this IEndpointRouteBuilder app)
    {
        // Danh sách ngành cho ô chọn ở trang cài đặt. Không có dữ liệu tenant nên chỉ cần đăng nhập.
        app.MapGet("/industries", () => TypedResults.Ok(
                IndustryCatalog.All.Select(i => new IndustryResponse(i.Slug, i.Name)).ToList()))
            .WithTags("Tenant")
            .RequireAuthorization();

        return app;
    }
}
