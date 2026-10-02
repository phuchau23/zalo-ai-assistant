using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Security;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Đăng ký options (validate khi khởi động), mã hóa, tenant context. Dùng chung cho Api và Worker.</summary>
    public static IServiceCollection AddZaloAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidatedOptions<AppOptions>(configuration, AppOptions.SectionName);
        services.AddValidatedOptions<SecurityOptions>(configuration, SecurityOptions.SectionName);
        services.AddValidatedOptions<AiOptions>(configuration, AiOptions.SectionName);
        services.AddValidatedOptions<ZaloOptions>(configuration, ZaloOptions.SectionName);

        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

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
