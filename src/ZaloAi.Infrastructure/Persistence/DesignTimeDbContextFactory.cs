using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Persistence;

/// <summary>
/// Chỉ dùng cho lệnh `dotnet ef` (tạo migration, update database). Không chạy lúc ứng dụng chạy thật.
/// Đọc connection string từ env ConnectionStrings__Postgres, mặc định là Postgres trong docker-compose.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DevConnectionString = "Host=localhost;Port=5432;Database=zaloai;Username=app;Password=app";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? DevConnectionString;
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseZaloAiPostgres(connectionString);
        return new AppDbContext(builder.Options, new TenantContext());
    }
}
