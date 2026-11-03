using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;

namespace ZaloAi.Api.Auth;

internal static class TenantAuthorization
{
    /// <summary>
    /// Endpoint cần đăng nhập + có tenant đang hoạt động + vai trò tối thiểu.
    /// Thiếu cookie → 401. Không có tenant → 403 "no_active_tenant". Thiếu quyền → 403 "forbidden".
    /// </summary>
    public static TBuilder RequireTenantRole<TBuilder>(this TBuilder builder, TenantRole minimumRole)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.TenantId is null || tenant.Role is not { } role)
            {
                throw ForbiddenException.NoActiveTenant();
            }

            if (role < minimumRole)
            {
                throw new ForbiddenException();
            }

            return await next(context);
        });
        return builder;
    }

    /// <summary>
    /// Endpoint cần đăng nhập + tenant + một quyền chi tiết (<see cref="TenantPermissions"/>). Chủ DN luôn qua; nhân viên phải được
    /// chủ bật công tắc quyền đó (trang Cài đặt → Nhân viên). Thiếu → 403 "forbidden".
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireTenantRole(TenantRole.Staff);
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenant.Can(permission))
            {
                throw new ForbiddenException();
            }

            return await next(context);
        });
        return builder;
    }
}
