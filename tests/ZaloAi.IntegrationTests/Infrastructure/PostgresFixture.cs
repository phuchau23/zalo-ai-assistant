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

    public const string TestPassword = "Test@123456";

    private ApiFactory? _api;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>API dùng chung cho các test (rate limit login nới rộng). Test rate limit tự tạo factory riêng.</summary>
    public ApiFactory Api => _api ??= new ApiFactory(ConnectionString);

    public static string OwnerEmail(Guid tenantId) => $"owner-{tenantId:N}@test.local";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext(tenantId: null);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

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
            Email = OwnerEmail(tenantId),
            PasswordHash = new PasswordHasher<User>().HashPassword(null!, TestPassword),
            Name = "Owner",
        };

        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, IndustrySlug = "spa", BotName = "Bot", BotPronoun = "em" });
        db.Users.Add(owner);
        db.Memberships.Add(new Membership { TenantId = tenantId, UserId = owner.Id, Role = TenantRole.Owner });
        await db.SaveChangesAsync();

        return (tenantId, owner.Id);
    }

    /// <summary>Thêm user (mới hoặc có sẵn theo email) vào tenant với vai trò cho trước. Trả về email.</summary>
    public async Task<string> AddMemberAsync(Guid tenantId, TenantRole role, string? email = null)
    {
        email ??= $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@test.local";
        await using var db = CreateDbContext(tenantId);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                PasswordHash = new PasswordHasher<User>().HashPassword(null!, TestPassword),
                Name = role.ToString(),
            };
            db.Users.Add(user);
        }

        db.Memberships.Add(new Membership { TenantId = tenantId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();
        return email;
    }

    public async Task RemoveMemberAsync(Guid tenantId, string email)
    {
        await using var db = CreateDbContext(tenantId);
        var membership = await db.Memberships.SingleAsync(m => m.User!.Email == email);
        db.Memberships.Remove(membership);
        await db.SaveChangesAsync();
    }

    public async Task SetTenantStatusAsync(Guid tenantId, TenantStatus status)
    {
        await using var db = CreateDbContext(tenantId);
        (await db.Tenants.SingleAsync()).Status = status;
        await db.SaveChangesAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
