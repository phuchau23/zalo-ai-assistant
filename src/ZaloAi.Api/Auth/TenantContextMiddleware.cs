using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Api.Auth;

/// <summary>
/// Sau khi cookie được xác thực: kiểm lại user + membership + trạng thái tenant trong DB, rồi set <see cref="ITenantContext"/>.
/// - User đã bị xóa → đăng xuất, request coi như chưa đăng nhập (401).
/// - Không còn là thành viên, hoặc tenant bị khóa → tenant context rỗng (endpoint cần tenant trả 403).
/// tenantId chỉ lấy từ cookie đã ký, không bao giờ từ body/query/route.
/// </summary>
internal sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext, TenantContext tenantContext, AccessQueries access)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true && AuthClaims.GetUserId(httpContext.User) is { } userId)
        {
            var user = await access.GetUserAsync(userId, httpContext.RequestAborted);
            if (user is null)
            {
                await httpContext.SignOutAsync();
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
            else
            {
                Guid? tenantId = null;
                TenantRole? role = null;
                if (AuthClaims.GetTenantId(httpContext.User) is { } claimedTenantId)
                {
                    var tenant = await access.GetTenantAccessAsync(userId, claimedTenantId, httpContext.RequestAborted);
                    if (tenant is { Status: TenantStatus.Active })
                    {
                        tenantId = tenant.TenantId;
                        role = tenant.Role;
                    }
                }

                tenantContext.Set(tenantId, userId, role, user.IsSuperAdmin);
            }
        }

        await next(httpContext);
    }
}
