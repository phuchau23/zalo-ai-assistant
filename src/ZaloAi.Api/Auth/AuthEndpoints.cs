using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Security;

namespace ZaloAi.Api.Auth;

internal static class AuthEndpoints
{
    // Hash giả để user không tồn tại vẫn tốn thời gian kiểm mật khẩu như user thật (không đoán được email nào có tài khoản).
    private static readonly User DummyUser = new() { Email = "", PasswordHash = "", Name = "" };
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, Guid.NewGuid().ToString());

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitSettings.LoginPolicy)
            .Validate<LoginRequest>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/logout", LogoutAsync).RequireAuthorization();

        group.MapGet("/me", MeAsync).RequireAuthorization().ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/switch-tenant", SwitchTenantAsync)
            .RequireAuthorization()
            .Validate<SwitchTenantRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitSettings.LoginPolicy)
            .Validate<ChangePasswordRequest>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    /// <summary>
    /// Tự đổi mật khẩu (kiểm mật khẩu hiện tại). Đổi xong mọi phiên khác bị đăng xuất (dấu vân tay mật khẩu đổi); phiên này được
    /// cấp lại cookie mới để không phải đăng nhập lại.
    /// </summary>
    private static async Task<NoContent> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext httpContext,
        ITenantContext tenant,
        UserRepository users,
        AuditLogRepository audit,
        IPasswordHasher<User> hasher,
        CancellationToken cancellationToken)
    {
        var userId = tenant.UserId ?? throw new InvalidOperationException("Request đã xác thực nhưng thiếu UserId.");
        var user = await users.GetAsync(userId, cancellationToken) ?? throw new NotFoundException();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            audit.AddAuthEvent(tenant.TenantId, userId, "auth.password_change_failed");
            await audit.SaveChangesAsync(cancellationToken);
            throw new InvalidInputException("Mật khẩu hiện tại không đúng.");
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        audit.AddAuthEvent(tenant.TenantId, userId, "auth.password_changed");
        await users.SaveChangesAsync(cancellationToken);
        await AuthClaims.SignInAsync(httpContext, userId, AuthClaims.GetTenantId(httpContext.User), Passwords.Stamp(user.PasswordHash));
        return TypedResults.NoContent();
    }

    private static async Task<Ok<MeResponse>> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        UserRepository users,
        AccessQueries access,
        AuditLogRepository audit,
        IPasswordHasher<User> hasher,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email, cancellationToken);
        if (user is null)
        {
            hasher.VerifyHashedPassword(DummyUser, DummyHash, request.Password);
            audit.AddAuthEvent(null, null, "auth.login_failed");
            await audit.SaveChangesAsync(cancellationToken);
            throw new InvalidCredentialsException();
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            audit.AddAuthEvent(null, user.Id, "auth.login_failed");
            await audit.SaveChangesAsync(cancellationToken);
            throw new InvalidCredentialsException();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
        }

        var tenants = await access.ListTenantsForUserAsync(user.Id, cancellationToken);
        var current = tenants.FirstOrDefault(t => t.Status == TenantStatus.Active);

        await AuthClaims.SignInAsync(httpContext, user.Id, current?.TenantId, Passwords.Stamp(user.PasswordHash));
        audit.AddAuthEvent(current?.TenantId, user.Id, "auth.login");
        await audit.SaveChangesAsync(cancellationToken);

        var me = MeResponse.From(new UserAccess(user.Id, user.Email, user.Name, user.IsSuperAdmin), tenants, current?.TenantId);
        return TypedResults.Ok(me);
    }

    private static async Task<NoContent> LogoutAsync(
        HttpContext httpContext,
        ITenantContext tenant,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        audit.AddAuthEvent(tenant.TenantId, tenant.UserId, "auth.logout");
        await audit.SaveChangesAsync(cancellationToken);
        await httpContext.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Ok<MeResponse>> MeAsync(ITenantContext tenant, AccessQueries access, CancellationToken cancellationToken)
    {
        var userId = tenant.UserId ?? throw new InvalidOperationException("Request đã xác thực nhưng thiếu UserId.");
        var user = await access.GetUserAsync(userId, cancellationToken) ?? throw new InvalidOperationException("User không tồn tại.");
        var tenants = await access.ListTenantsForUserAsync(userId, cancellationToken);
        return TypedResults.Ok(MeResponse.From(user, tenants, tenant.TenantId));
    }

    private static async Task<Ok<MeResponse>> SwitchTenantAsync(
        SwitchTenantRequest request,
        HttpContext httpContext,
        ITenantContext tenant,
        AccessQueries access,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var userId = tenant.UserId ?? throw new InvalidOperationException("Request đã xác thực nhưng thiếu UserId.");

        // Không phải thành viên và tenant không tồn tại trả cùng 404, để không lộ tenant nào có thật.
        var target = await access.GetTenantAccessAsync(userId, request.TenantId, cancellationToken);
        if (target is not { Status: TenantStatus.Active })
        {
            throw new NotFoundException();
        }

        var user = await access.GetUserAsync(userId, cancellationToken) ?? throw new InvalidOperationException("User không tồn tại.");
        await AuthClaims.SignInAsync(httpContext, userId, target.TenantId, user.PasswordStamp);
        audit.AddAuthEvent(target.TenantId, userId, "auth.tenant_switched");
        await audit.SaveChangesAsync(cancellationToken);

        var tenants = await access.ListTenantsForUserAsync(userId, cancellationToken);
        return TypedResults.Ok(MeResponse.From(user, tenants, target.TenantId));
    }
}
