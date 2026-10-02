var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Tạm thời: /health đầy đủ (kiểm tra Postgres, Redis) làm ở bước 4.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();

// Cho WebApplicationFactory trong IntegrationTests truy cập.
public partial class Program;
