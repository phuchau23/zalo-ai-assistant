using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Infrastructure.Persistence.Seed;

/// <summary>
/// Dữ liệu mẫu cho dev: chạy migration + tạo bảng Hangfire, tạo super admin + 2 tenant mẫu, mỗi tenant 1 owner.
/// Chạy lại nhiều lần không tạo trùng. Từ chối chạy ngoài môi trường Development.
/// </summary>
public static class DevSeeder
{
    public const string DevPassword = "Dev@123456";
    public const string SuperAdminEmail = "admin@zaloai.local";

    public static readonly IReadOnlyList<SampleTenant> SampleTenants =
    [
        new(Guid.Parse("0199a000-0000-7000-8000-000000000001"), "Khoa Học Nguyệt Đạo", "spa", "Nguyệt Đạo", "em", "owner@nguyetdao.local"),
        new(Guid.Parse("0199a000-0000-7000-8000-000000000002"), "Sửa nhà An Phát", "sua-nha", "An Phát", "em", "owner@anphat.local"),
    ];

    public static async Task RunAsync(IServiceProvider services, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Lệnh seed chỉ chạy ở môi trường Development.");
        }

        var hasher = services.GetRequiredService<IPasswordHasher<User>>();

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            await HangfireSetup.EnsureSchemaAsync(
                services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Postgres, cancellationToken);

            var users = scope.ServiceProvider.GetRequiredService<UserRepository>();
            if (await users.FindByEmailAsync(SuperAdminEmail, cancellationToken) is null)
            {
                users.Add(SuperAdminEmail, Hash(hasher), "Super Admin", isSuperAdmin: true);
                await users.SaveChangesAsync(cancellationToken);
            }
        }

        foreach (var sample in SampleTenants)
        {
            // Mỗi tenant một scope riêng, set tenant trước khi ghi: seed cũng đi qua đúng luật cô lập như code thật.
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(sample.Id, null, null, isSuperAdmin: false);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Tenants.AnyAsync(t => t.Id == sample.Id, cancellationToken))
            {
                continue;
            }

            db.Tenants.Add(new Tenant
            {
                Id = sample.Id,
                Name = sample.Name,
                IndustrySlug = sample.IndustrySlug,
                BotName = sample.BotName,
                BotPronoun = sample.BotPronoun,
            });

            var users = scope.ServiceProvider.GetRequiredService<UserRepository>();
            var owner = await users.FindByEmailAsync(sample.OwnerEmail, cancellationToken)
                ?? users.Add(sample.OwnerEmail, Hash(hasher), $"Chủ {sample.Name}");

            scope.ServiceProvider.GetRequiredService<MembershipRepository>().Add(sample.Id, owner.Id, TenantRole.Owner);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string Hash(IPasswordHasher<User> hasher) =>
        hasher.HashPassword(new User { Email = "", PasswordHash = "", Name = "" }, DevPassword);

    public sealed record SampleTenant(Guid Id, string Name, string IndustrySlug, string BotName, string BotPronoun, string OwnerEmail);
}
