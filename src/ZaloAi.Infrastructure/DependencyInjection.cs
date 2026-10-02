using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Security;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Đăng ký options (validate khi khởi động), mã hóa, tenant context, DB, repository. Dùng chung cho Api và Worker.</summary>
    public static IServiceCollection AddZaloAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidatedOptions<AppOptions>(configuration, AppOptions.SectionName);
        services.AddValidatedOptions<SecurityOptions>(configuration, SecurityOptions.SectionName);
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<JobsOptions>(configuration, JobsOptions.SectionName);
        services.AddValidatedOptions<AiOptions>(configuration, AiOptions.SectionName);
        services.AddValidatedOptions<ZaloOptions>(configuration, ZaloOptions.SectionName);

        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseZaloAiPostgres(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Postgres));

        services.AddScoped<TenantRepository>();
        services.AddScoped<MembershipRepository>();
        services.AddScoped<AuditLogRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<AccessQueries>();

        services.AddZaloAiHangfire();
        services.AddScoped<SampleTenantJob>();

        return services;
    }

    private static void AddValidatedOptions<T>(this IServiceCollection services, IConfiguration configuration, string section)
        where T : class
    {
        services.AddOptions<T>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
