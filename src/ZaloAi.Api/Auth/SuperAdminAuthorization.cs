using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;

namespace ZaloAi.Api.Auth;

internal static class SuperAdminAuthorization
{
    /// <summary>Endpoint chỉ dành cho super admin (cờ trên users, chỉ chủ dự án). Thiếu cookie → 401; không phải super admin → 403.</summary>
    public static TBuilder RequireSuperAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsSuperAdmin)
            {
                throw new ForbiddenException();
            }

            return await next(context);
        });
        return builder;
    }
}
