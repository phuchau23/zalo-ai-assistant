using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Sentry;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace ZaloAi.Infrastructure.Logging;

public static class LoggingSetup
{
    public const string SentryDsnKey = "Sentry:Dsn";

    /// <summary>
    /// Cấu hình Serilog chung cho Api và Worker. Mức log chỉnh qua section "Serilog" trong appsettings.
    /// Có Sentry:Dsn → log mức Error gửi lên Sentry (đã che dữ liệu), log Information làm "breadcrumb" (dấu vết trước lỗi).
    /// </summary>
    /// <param name="initializeSentrySdk">
    /// Worker: true (Serilog khởi tạo Sentry). Api: false, vì Sentry.AspNetCore đã khởi tạo (kèm thông tin request).
    /// </param>
    public static LoggerConfiguration ConfigureZaloAi(
        this LoggerConfiguration logger,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool initializeSentrySdk)
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

        if (GetSentryDsn(configuration) is { } dsn)
        {
            logger.WriteTo.Sentry(options =>
            {
                options.InitializeSdk = initializeSentrySdk;
                if (initializeSentrySdk)
                {
                    ApplySentryDefaults(options, dsn, environment);
                }

                options.MinimumEventLevel = LogEventLevel.Error;
                options.MinimumBreadcrumbLevel = LogEventLevel.Information;
            });
        }

        // Dev: dạng chữ dễ đọc. Môi trường khác: JSON để máy đọc/tìm kiếm.
        return environment.IsDevelopment()
            ? logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            : logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }

    /// <summary>Để trống Sentry:Dsn (máy dev, test) = tắt Sentry.</summary>
    public static string? GetSentryDsn(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var dsn = configuration[SentryDsnKey];
        return string.IsNullOrWhiteSpace(dsn) ? null : dsn;
    }

    /// <summary>Cấu hình Sentry dùng chung: không gửi PII mặc định, che dữ liệu trước khi gửi.</summary>
    public static void ApplySentryDefaults(SentryOptions options, string dsn, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        options.Dsn = dsn;
        options.Environment = environment.EnvironmentName.ToLowerInvariant();
        options.SendDefaultPii = false;
        options.SetBeforeSend((sentryEvent, _) => SentryScrubber.Scrub(sentryEvent));
        options.SetBeforeBreadcrumb((breadcrumb, _) => SentryScrubber.Scrub(breadcrumb));
    }
}
