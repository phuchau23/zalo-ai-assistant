using System.Text.Json;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Api.Tenants;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Api;

internal static class ApiSetup
{
    public const string SessionCookieName = "zaloai.session";

    public static IServiceCollection AddZaloAiApi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<RateLimitSettings>()
            .Bind(configuration.GetSection(RateLimitSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddProblemDetails();
        services.AddExceptionHandler<AppExceptionHandler>();

        // Lỗi validate trả tên trường dạng camelCase, khớp JSON mà FE gửi.
        ValidatorOptions.Global.PropertyNameResolver = (_, member, _) =>
            member is null ? null : JsonNamingPolicy.CamelCase.ConvertName(member.Name);
        services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>(includeInternalTypes: true);

        // Khóa mã hóa cookie lưu trong Postgres: deploy lại hay chạy nhiều bản API không làm mọi người bị đăng xuất.
        services.AddDataProtection()
            .SetApplicationName("zaloai")
            .PersistKeysToDbContext<AppDbContext>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                // Lax (không phải Strict): sau M4, Zalo redirect về callback OAuth vẫn mang được cookie.
                // Cùng với việc API chỉ nhận JSON, Lax chặn được CSRF từ form của trang khác.
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() || environment.IsEnvironment("Testing")
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;

                // API không redirect sang trang login, trả 401/403 dạng ProblemDetails.
                options.Events.OnRedirectToLogin = ctx =>
                    Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized, "unauthenticated", "Chưa đăng nhập.");
                options.Events.OnRedirectToAccessDenied = ctx =>
                    Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "Không có quyền thực hiện.");
            });
        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.OnRejected = (ctx, _) => new ValueTask(Problems.WriteAsync(
                ctx.HttpContext, StatusCodes.Status429TooManyRequests, "rate_limited", "Thử quá nhiều lần, vui lòng đợi một phút."));

            // TODO(deploy): sau reverse proxy (Railway/VPS), cấu hình ForwardedHeaders để RemoteIpAddress là IP thật của khách.
            options.AddPolicy(RateLimitSettings.LoginPolicy, http =>
            {
                var permit = http.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value.LoginPermitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });

        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres");
        services.AddOpenApi();

        return services;
    }

    public static WebApplication UseZaloAiApi(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseMiddleware<TenantContextMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapHealthChecks("/health").AllowAnonymous();
        if (!app.Environment.IsProduction())
        {
            // FE chạy `pnpm gen:api` đọc file này để sinh type.
            app.MapOpenApi();
        }

        app.MapAuthEndpoints();
        app.MapTenantSettingsEndpoints();

        return app;
    }
}
