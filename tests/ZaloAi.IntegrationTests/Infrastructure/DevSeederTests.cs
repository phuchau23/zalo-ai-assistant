using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Persistence.Seed;

namespace ZaloAi.IntegrationTests.Infrastructure;

[Collection(PostgresGroup.Name)]
public sealed class DevSeederTests(PostgresFixture db)
{
    private static HostingEnvironment Env(string name) => new() { EnvironmentName = name, ApplicationName = "test" };

    [Fact]
    public async Task Seed_is_idempotent_and_creates_sample_tenants_with_owner()
    {
        await using var services = db.BuildServices();

        await DevSeeder.RunAsync(services, Env(Environments.Development), CancellationToken.None);
        await DevSeeder.RunAsync(services, Env(Environments.Development), CancellationToken.None);

        foreach (var sample in DevSeeder.SampleTenants)
        {
            await using var asTenant = db.CreateDbContext(sample.Id);
            (await asTenant.Tenants.SingleAsync()).Name.ShouldBe(sample.Name);
            var membership = await asTenant.Memberships.Include(m => m.User).SingleAsync();
            membership.User!.Email.ShouldBe(sample.OwnerEmail);

            var hasher = new PasswordHasher<User>();
            hasher.VerifyHashedPassword(membership.User, membership.User.PasswordHash, DevSeeder.DevPassword)
                .ShouldBe(PasswordVerificationResult.Success);
        }

        await using var global = db.CreateDbContext(tenantId: null);
        (await global.Users.CountAsync(u => u.Email == DevSeeder.SuperAdminEmail && u.IsSuperAdmin)).ShouldBe(1);
    }

    [Fact]
    public async Task Seed_refuses_to_run_outside_development()
    {
        await using var services = db.BuildServices();

        await Should.ThrowAsync<InvalidOperationException>(
            () => DevSeeder.RunAsync(services, Env(Environments.Production), CancellationToken.None));
    }
}
