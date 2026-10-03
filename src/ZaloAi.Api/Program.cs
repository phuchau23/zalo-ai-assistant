using Serilog;
using ZaloAi.Api;
using ZaloAi.Infrastructure;
using ZaloAi.Infrastructure.Logging;
using ZaloAi.Infrastructure.Persistence.Seed;

// Logger tạm để ghi lỗi xảy ra trước khi host dựng xong (ví dụ thiếu cấu hình).
Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: null).CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, logger) =>
        logger.ConfigureZaloAi(builder.Configuration, builder.Environment, initializeSentrySdk: false).ReadFrom.Services(services));

    if (LoggingSetup.GetSentryDsn(builder.Configuration) is { } sentryDsn)
    {
        builder.WebHost.UseSentry(options =>
        {
            LoggingSetup.ApplySentryDefaults(options, sentryDsn, builder.Environment);
            // Lỗi đã được gửi qua Serilog (một nguồn duy nhất, đã che dữ liệu); tắt kênh log riêng của Sentry để không gửi trùng.
            options.MinimumEventLevel = LogLevel.None;
            options.MinimumBreadcrumbLevel = LogLevel.None;
            options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
        });
    }

    builder.Services.AddZaloAiInfrastructure(builder.Configuration);
    builder.Services.AddZaloAiApi(builder.Configuration, builder.Environment);

    var app = builder.Build();

    // `dotnet run --project src/ZaloAi.Api -- seed`: migrate + dữ liệu mẫu (chỉ Development), rồi thoát.
    if (args is ["seed", ..])
    {
        await DevSeeder.RunAsync(app.Services, app.Environment, CancellationToken.None);
        // Console thay vì logger: logger che mọi giá trị có tên chứa "password".
        Console.WriteLine($"Seed xong. Mật khẩu mọi tài khoản mẫu: {DevSeeder.DevPassword}");
        return;
    }

    // Chỉ ghi method, path (không có query string), status, thời gian.
    app.UseSerilogRequestLogging();

    app.UseZaloAiApi();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API dừng do lỗi khi khởi động");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Cho WebApplicationFactory trong IntegrationTests truy cập.
public partial class Program;
