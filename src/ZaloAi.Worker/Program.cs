using Serilog;
using ZaloAi.Ai;
using ZaloAi.Channels;
using ZaloAi.Infrastructure;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Logging;

// Logger tạm để ghi lỗi xảy ra trước khi host dựng xong (ví dụ thiếu cấu hình).
Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: null).CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, logger) =>
        logger.ConfigureZaloAi(builder.Configuration, builder.Environment, initializeSentrySdk: true).ReadFrom.Services(services));

    builder.Services.AddZaloAiInfrastructure(builder.Configuration);
    builder.Services.AddZaloAiAi();
    builder.Services.AddZaloAiChannels();

    // Worker là nơi duy nhất chạy job; Api chỉ đẩy job vào hàng đợi.
    builder.Services.AddZaloAiJobServer();
    builder.Services.AddZaloAiTelegramPolling();

    var host = builder.Build();
    HangfireSetup.RegisterRecurringJobs(host.Services);
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
