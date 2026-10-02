using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;

namespace ZaloAi.Api.Jobs;

public sealed record EnqueuedJobResponse(string JobId);

/// <summary>Endpoint thử hàng đợi, chỉ map ở Development/Testing.</summary>
internal static class DevJobEndpoints
{
    public static IEndpointRouteBuilder MapDevJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/jobs/sample", EnqueueSample)
            .WithTags("Dev")
            .RequireTenantRole(TenantRole.Owner);

        return app;
    }

    // tenantId lấy từ cookie (ITenantContext), job chạy trong đúng tenant đó.
    private static Accepted<EnqueuedJobResponse> EnqueueSample(ITenantContext tenant, IBackgroundJobClient jobs)
    {
        var tenantId = tenant.RequireTenantId();
        var jobId = jobs.Enqueue<SampleTenantJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        return TypedResults.Accepted((string?)null, new EnqueuedJobResponse(jobId));
    }
}
