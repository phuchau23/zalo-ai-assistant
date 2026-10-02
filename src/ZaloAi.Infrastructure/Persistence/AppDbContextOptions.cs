using Microsoft.EntityFrameworkCore;

namespace ZaloAi.Infrastructure.Persistence;

/// <summary>Cấu hình Npgsql dùng chung cho DI, công cụ `dotnet ef` và test.</summary>
public static class AppDbContextOptions
{
    public static DbContextOptionsBuilder UseZaloAiPostgres(this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            })
            .UseSnakeCaseNamingConvention();
    }
}
