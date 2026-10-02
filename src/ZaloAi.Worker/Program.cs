var builder = Host.CreateApplicationBuilder(args);

// Hangfire server đăng ký ở bước 5.

var host = builder.Build();
await host.RunAsync();
