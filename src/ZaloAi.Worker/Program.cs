using Serilog;
using ZaloAi.Infrastructure;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Logging;

// Logger tạm để ghi lỗi xảy ra trước khi host dựng xong (ví dụ thiếu cấu hình).
Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: null).CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, logger) =>
        logger.ConfigureZaloAi(builder.Configuration, builder.Environment).ReadFrom.Services(services));

    builder.Services.AddZaloAiInfrastructure(builder.Configuration);

    // Worker là nơi duy nhất chạy job; Api chỉ đẩy job vào hàng đợi.
    builder.Services.AddZaloAiJobServer();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker dừng do lỗi khi khởi động");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
