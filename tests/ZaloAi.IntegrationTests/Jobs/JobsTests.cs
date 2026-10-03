using System.Net;
using System.Net.Http.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ZaloAi.Ai;
using ZaloAi.Api.Jobs;
using ZaloAi.Infrastructure;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Api;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Jobs;

[Collection(PostgresGroup.Name)]
public sealed class JobsTests(PostgresFixture db)
{
    /// <summary>Dựng Worker giống thật (cùng DI, cùng Hangfire server), trỏ vào DB test, hỏi hàng đợi mỗi giây.</summary>
    private IHost StartWorker()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:AdminUrl"] = "http://localhost:3000",
            ["App:ApiUrl"] = "http://localhost:4000",
            ["Security:EncryptionKey"] = ApiFactory.NewEncryptionKey(),
            ["ConnectionStrings:Postgres"] = db.ConnectionString,
            ["Jobs:QueuePollSeconds"] = "1",
            ["Jobs:WorkerCount"] = "2",
            ["Ai:EmbedProvider"] = "fake",
            ["Storage:LocalRoot"] = ApiFactory.TestStorageRoot,
        });
        builder.Services.AddZaloAiInfrastructure(builder.Configuration);
        builder.Services.AddZaloAiAi();
        builder.Services.AddZaloAiJobServer();
        var host = builder.Build();
        host.Start();
        return host;
    }

    [Fact]
    public async Task Job_enqueued_by_api_runs_in_worker_inside_its_own_tenant()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Job A");
        var (tenantB, _) = await db.CreateTenantAsync("Job B");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));

        using var response = await client.PostAsync(new Uri("/dev/jobs/sample", UriKind.Relative), null);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<EnqueuedJobResponse>()).ShouldNotBeNull().JobId.ShouldNotBeNullOrEmpty();

        using var worker = StartWorker();
        try
        {
            await using var asA = db.CreateDbContext(tenantA);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await asA.AuditLogs.AnyAsync(a => a.TenantId == tenantA && a.Action == SampleTenantJob.AuditAction))
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline, "Worker không chạy job trong 30 giây.");
                await Task.Delay(500);
            }

            await using var asB = db.CreateDbContext(tenantB);
            (await asB.AuditLogs.AnyAsync(a => a.TenantId == tenantB && a.Action == SampleTenantJob.AuditAction)).ShouldBeFalse();
        }
        finally
        {
            await worker.StopAsync();
        }
    }

    [Fact]
    public async Task Staff_cannot_enqueue_owner_only_job()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Job Staff");
        var staffEmail = await db.AddMemberAsync(tenantId, Core.Tenancy.TenantRole.Staff);
        using var client = await db.Api.CreateLoggedInClientAsync(staffEmail);

        using var response = await client.PostAsync(new Uri("/dev/jobs/sample", UriKind.Relative), null);

        await response.ShouldBeProblemAsync(403, "forbidden");
    }

    [Fact]
    public async Task Global_retry_policy_is_5_attempts_with_backoff_and_registered_once()
    {
        // Dựng 2 host trong cùng process (như khi scale hoặc trong test): filter không được nhân đôi.
        for (var i = 0; i < 2; i++)
        {
            using var worker = StartWorker();
            await worker.StopAsync();
        }

        var retry = GlobalJobFilters.Filters.Select(f => f.Instance).OfType<AutomaticRetryAttribute>().ShouldHaveSingleItem();
        retry.Attempts.ShouldBe(5);
        retry.DelaysInSeconds.ShouldBe([10, 30, 90, 270, 810]);
        retry.OnAttemptsExceeded.ShouldBe(AttemptsExceededAction.Fail);
        GlobalJobFilters.Filters.Select(f => f.Instance).OfType<JobFailureAlertFilter>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Hangfire_dashboard_is_only_for_super_admin()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Dashboard");
        var superAdminEmail = await db.CreateSuperAdminAsync();
        var dashboard = new Uri("/hangfire", UriKind.Relative);

        using var anonymous = db.Api.CreateClient();
        using var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var superAdmin = await db.Api.CreateLoggedInClientAsync(superAdminEmail);

        using (var response = await anonymous.GetAsync(dashboard))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await owner.GetAsync(dashboard))
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }

        using (var response = await superAdmin.GetAsync(dashboard))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
