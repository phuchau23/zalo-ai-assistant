using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace ZaloAi.Infrastructure.Logging;

public static class LoggingSetup
{
    /// <summary>Cấu hình Serilog chung cho Api và Worker. Mức log chỉnh qua section "Serilog" trong appsettings.</summary>
    public static LoggerConfiguration ConfigureZaloAi(
        this LoggerConfiguration logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(environment);

        logger
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", environment.ApplicationName)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            // Đặt cuối cùng để che cả property do các enricher trước thêm vào.
            .Enrich.With<SensitiveDataRedactor>();

        // Dev: dạng chữ dễ đọc. Môi trường khác: JSON để máy đọc/tìm kiếm.
        return environment.IsDevelopment()
            ? logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            : logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
}
