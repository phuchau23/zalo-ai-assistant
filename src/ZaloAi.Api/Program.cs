using Serilog;
using ZaloAi.Infrastructure;
using ZaloAi.Infrastructure.Logging;
using ZaloAi.Infrastructure.Persistence.Seed;

// Logger tạm để ghi lỗi xảy ra trước khi host dựng xong (ví dụ thiếu cấu hình).
Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: null).CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog((services, logger) =>
        logger.ConfigureZaloAi(builder.Configuration, builder.Environment).ReadFrom.Services(services));

    builder.Services.AddZaloAiInfrastructure(builder.Configuration);

    var app = builder.Build();

    // `dotnet run --project src/ZaloAi.Api -- seed`: migrate + dữ liệu mẫu (chỉ Development), rồi thoát.
    if (args is ["seed", ..])
    {
        await DevSeeder.RunAsync(app.Services, app.Environment, CancellationToken.None);
        Log.Information("Seed xong. Mật khẩu mọi tài khoản mẫu: {DevPassword}", DevSeeder.DevPassword);
        return;
    }

    // Chỉ ghi method, path (không có query string), status, thời gian.
    app.UseSerilogRequestLogging();

    // Tạm thời: /health đầy đủ (kiểm tra Postgres, Redis) làm ở bước 4.
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
