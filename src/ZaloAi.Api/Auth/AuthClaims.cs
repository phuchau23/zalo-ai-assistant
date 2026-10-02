using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ZaloAi.Api.Auth;

/// <summary>
/// Cookie chỉ chứa userId và tenant đang chọn. Vai trò, trạng thái tenant, cờ super admin đọc lại từ DB mỗi request
/// (xem <see cref="TenantContextMiddleware"/>), để thu hồi quyền có hiệu lực ngay.
/// </summary>
internal static class AuthClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";

    public static Guid? GetUserId(ClaimsPrincipal principal) => Parse(principal.FindFirstValue(UserId));

    public static Guid? GetTenantId(ClaimsPrincipal principal) => Parse(principal.FindFirstValue(TenantId));

    public static Task SignInAsync(HttpContext httpContext, Guid userId, Guid? tenantId)
    {
        List<Claim> claims = [new(UserId, userId.ToString())];
        if (tenantId is not null)
        {
            claims.Add(new Claim(TenantId, tenantId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static Guid? Parse(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
