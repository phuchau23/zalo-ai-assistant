using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ZaloAi.Core.Options;

namespace ZaloAi.Infrastructure.Jobs;

public static class HangfireSetup
{
    public const string SchemaName = "hangfire";

    private static readonly Lock GlobalFiltersGate = new();

    /// <summary>
    /// Hangfire lưu job trong Postgres, schema riêng "hangfire" (Hangfire tự tạo bảng, không qua migration EF).
    /// Api chỉ gọi hàm này (đẩy job). Worker gọi thêm <see cref="AddZaloAiJobServer"/> (chạy job).
    /// </summary>
    public static IServiceCollection AddZaloAiHangfire(this IServiceCollection services)
    {
        services.AddHangfire((sp, config) =>
        {
            var database = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var jobs = sp.GetRequiredService<IOptions<JobsOptions>>().Value;

            // Tạo filter ở đây (lúc host dựng cấu hình) để chúng dùng logger của đúng host này.
            ConfigureGlobalFilters();

            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(
                    bootstrap => bootstrap.UseNpgsqlConnection(database.Postgres),
                    new PostgreSqlStorageOptions
                    {
                        SchemaName = SchemaName,
                        // Chỉ Worker tạo/nâng cấp bảng. Api tạo bảng lúc khởi động thì DB chậm là API treo theo.
                        PrepareSchemaIfNecessary = sp.GetService<JobServerMarker>() is not null,
                        QueuePollInterval = TimeSpan.FromSeconds(jobs.QueuePollSeconds),
                    });
        });

        return services;
    }

    public static IServiceCollection AddZaloAiJobServer(this IServiceCollection services)
    {
        services.AddSingleton<JobServerMarker>();
        services.AddHangfireServer((sp, options) =>
        {
            options.Queues = JobQueues.All;
            options.WorkerCount = sp.GetRequiredService<IOptions<JobsOptions>>().Value.WorkerCount;
        });

        return services;
    }

    /// <summary>Job định kỳ (đăng ký ở Worker khi khởi động; đăng ký lại nhiều lần vô hại).</summary>
    public static void RegisterRecurringJobs(IServiceProvider services)
    {
        var recurring = services.GetRequiredService<IRecurringJobManager>();
        recurring.AddOrUpdate<IndexSweepJob>(
            IndexSweepJob.RecurringId,
            job => job.RunAsync(CancellationToken.None),
            "*/5 * * * *");
        recurring.AddOrUpdate<ZaloTokenSweepJob>(
            ZaloTokenSweepJob.RecurringId,
            job => job.RunAsync(CancellationToken.None),
            "*/30 * * * *");
    }

    /// <summary>Tạo/nâng cấp bảng Hangfire (dùng cho lệnh seed và test, để Api dùng được hàng đợi khi Worker chưa chạy lần nào).</summary>
    public static async Task EnsureSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        PostgreSqlObjectsInstaller.Install(connection, SchemaName);
    }

    // Filter của Hangfire là toàn cục (static). Thay AutomaticRetry mặc định (10 lần) bằng chính sách của dự án,
    // làm idempotent vì test có thể dựng host nhiều lần trong cùng process.
    private static void ConfigureGlobalFilters()
    {
        lock (GlobalFiltersGate)
        {
            foreach (var filter in GlobalJobFilters.Filters.Where(f => f.Instance is AutomaticRetryAttribute or JobFailureAlertFilter).ToList())
            {
                GlobalJobFilters.Filters.Remove(filter.Instance);
            }

            GlobalJobFilters.Filters.Add(JobRetryPolicy.Create());
            GlobalJobFilters.Filters.Add(new JobFailureAlertFilter());
        }
    }
}

/// <summary>Đánh dấu process này là Worker (chạy Hangfire server).</summary>
internal sealed class JobServerMarker;
