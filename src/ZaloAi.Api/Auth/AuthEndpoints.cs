using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;

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

        return app;
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

        await AuthClaims.SignInAsync(httpContext, user.Id, current?.TenantId);
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

        await AuthClaims.SignInAsync(httpContext, userId, target.TenantId);
        audit.AddAuthEvent(target.TenantId, userId, "auth.tenant_switched");
        await audit.SaveChangesAsync(cancellationToken);

        var user = await access.GetUserAsync(userId, cancellationToken) ?? throw new InvalidOperationException("User không tồn tại.");
        var tenants = await access.ListTenantsForUserAsync(userId, cancellationToken);
        return TypedResults.Ok(MeResponse.From(user, tenants, target.TenantId));
    }
}
