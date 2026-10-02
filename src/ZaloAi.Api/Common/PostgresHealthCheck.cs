using Microsoft.Extensions.Diagnostics.HealthChecks;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Api.Common;

internal sealed class PostgresHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Không kết nối được Postgres.");
}
