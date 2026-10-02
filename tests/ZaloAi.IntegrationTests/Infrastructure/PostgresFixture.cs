using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.IntegrationTests.Infrastructure;

/// <summary>
/// Một Postgres (pgvector) tạm trong Docker cho cả bộ test, đã chạy migration. Cần Docker Desktop đang chạy.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext(tenantId: null);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AppDbContext CreateDbContext(Guid? tenantId) => CreateDbContext(CreateTenantContext(tenantId));

    public AppDbContext CreateDbContext(ITenantContext tenantContext)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseZaloAiPostgres(ConnectionString);
        return new AppDbContext(builder.Options, tenantContext);
    }

    public static TenantContext CreateTenantContext(Guid? tenantId)
    {
        var context = new TenantContext();
        if (tenantId is not null)
        {
            context.Set(tenantId, userId: null, TenantRole.Owner, isSuperAdmin: false);
        }

        return context;
    }

    /// <summary>Service provider giống ứng dụng thật (DI của Infrastructure), trỏ vào DB test.</summary>
    public ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:AdminUrl"] = "http://localhost:3000",
                ["App:ApiUrl"] = "http://localhost:4000",
                ["Security:EncryptionKey"] = ApiFactory.NewEncryptionKey(),
                ["ConnectionStrings:Postgres"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZaloAiInfrastructure(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Tạo tenant mới (kèm 1 owner) đi qua đúng luật cô lập: set tenant trước khi ghi.</summary>
    public async Task<(Guid TenantId, Guid OwnerId)> CreateTenantAsync(string name)
    {
        var tenantId = Guid.CreateVersion7();
        await using var db = CreateDbContext(tenantId);

        var owner = new User
        {
            Id = Guid.CreateVersion7(),
            Email = $"owner-{tenantId:N}@test.local",
            PasswordHash = new PasswordHasher<User>().HashPassword(null!, "x"),
            Name = "Owner",
        };

        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, IndustrySlug = "spa", BotName = "Bot", BotPronoun = "em" });
        db.Users.Add(owner);
        db.Memberships.Add(new Membership { TenantId = tenantId, UserId = owner.Id, Role = TenantRole.Owner });
        await db.SaveChangesAsync();

        return (tenantId, owner.Id);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
