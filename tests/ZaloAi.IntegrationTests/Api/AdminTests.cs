using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Admin;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Super admin (M6 đợt B3): chỉ super admin; tạo DN + chủ; gói/hạn/hạn mức; khóa/mở; tự khóa khi quá hạn; xuất; xóa DN.</summary>
[Collection(PostgresGroup.Name)]
public sealed class AdminTests(PostgresFixture db)
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<HttpClient> AdminAsync() => await db.Api.CreateLoggedInClientAsync(await db.CreateSuperAdminAsync());

    [Fact]
    public async Task Only_super_admin_can_open_admin_pages()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Không phải admin");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        (await owner.GetAsync(Url("/admin/tenants"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await db.Api.CreateClient().GetAsync(Url("/admin/tenants"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var admin = await AdminAsync();
        var tenants = (await admin.GetFromJsonAsync<List<AdminTenantItem>>(Url("/admin/tenants")))!;
        var item = tenants.Single(t => t.Id == tenantId);
        item.OwnerEmail.ShouldBe(PostgresFixture.OwnerEmail(tenantId));
        item.Quota.ShouldBe(100); // gói dùng thử
        (await admin.GetFromJsonAsync<List<PlanResponse>>(Url("/admin/plans")))!.ShouldContain(p => p.Code == "trial");
    }

    [Fact]
    public async Task Create_tenant_gives_one_time_password_and_owner_can_log_in()
    {
        var admin = await AdminAsync();
        var email = $"chu-{Guid.NewGuid():N}@test.local";
        using var response = await admin.PostAsJsonAsync(Url("/admin/tenants"), new CreateTenantRequest("Spa Mới", "spa", "Mai", email, "Chị Mai", "trial", null, 300));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = (await response.Content.ReadFromJsonAsync<CreateTenantResponse>())!;
        created.TemporaryPassword.ShouldNotBeNullOrEmpty();

        var owner = db.Api.CreateClient();
        (await owner.PostAsJsonAsync(Url("/auth/login"), new { email, password = created.TemporaryPassword })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync(Url("/tenant/settings"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = (await admin.GetFromJsonAsync<AdminTenantDetail>(Url($"/admin/tenants/{created.TenantId}")))!;
        detail.Tenant.Quota.ShouldBe(300);
        detail.Members.ShouldHaveSingleItem().Role.ShouldBe("owner");
        detail.MonthlyCosts.Count.ShouldBe(12);

        (await admin.PostAsJsonAsync(Url("/admin/tenants"), new CreateTenantRequest("X", "khong-co", null, email, "A", "trial", null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(Url("/admin/tenants"), new CreateTenantRequest("X", "spa", null, email, "A", "vip-khong-co", null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Suspend_blocks_the_business_and_resume_restores_it()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Khóa mở");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var admin = await AdminAsync();

        (await admin.PostAsync(Url($"/admin/tenants/{tenantId}/suspend"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await owner.GetAsync(Url("/tenant/settings"))).ShouldBeProblemAsync(403, "no_active_tenant");

        (await admin.PostAsync(Url($"/admin/tenants/{tenantId}/resume"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetAsync(Url("/tenant/settings"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Expired_tenant_is_suspended_after_grace_days_and_renewal_reopens_it()
    {
        var (expired, _) = await db.CreateTenantAsync("Quá hạn 4 ngày");
        var (grace, _) = await db.CreateTenantAsync("Quá hạn 1 ngày");
        await SetExpiryAsync(expired, DateTimeOffset.UtcNow.AddDays(-4));
        await SetExpiryAsync(grace, DateTimeOffset.UtcNow.AddDays(-1));

        using (var scope = db.Api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TenantExpiryJob>().RunAsync(CancellationToken.None);
        }

        var admin = await AdminAsync();
        var tenants = (await admin.GetFromJsonAsync<List<AdminTenantItem>>(Url("/admin/tenants")))!;
        tenants.Single(t => t.Id == expired).ShouldSatisfyAllConditions(t => t.Status.ShouldBe("suspended"), t => t.SuspendedReason.ShouldBe("expired"));
        tenants.Single(t => t.Id == grace).Status.ShouldBe("active");

        (await admin.PutAsJsonAsync(Url($"/admin/tenants/{expired}/subscription"), new UpdateSubscriptionRequest("trial", DateTimeOffset.UtcNow.AddMonths(1), null)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetFromJsonAsync<AdminTenantDetail>(Url($"/admin/tenants/{expired}")))!.Tenant.Status.ShouldBe("active");
    }

    [Fact]
    public async Task Export_then_purge_removes_everything_of_one_business_only()
    {
        var (tenantId, ownerId) = await db.CreateTenantAsync("Ngừng thuê");
        var (other, _) = await db.CreateTenantAsync("Vẫn thuê");
        await using (var context = db.CreateDbContext(tenantId))
        {
            context.Contacts.Add(new Contact { Id = Guid.CreateVersion7(), TenantId = tenantId, Channel = ChannelKind.Zalo, ExternalUserId = "u1" });
            await context.SaveChangesAsync();
        }

        var admin = await AdminAsync();
        using (var export = await admin.GetAsync(Url($"/admin/tenants/{tenantId}/export")))
        {
            export.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var zip = new ZipArchive(await export.Content.ReadAsStreamAsync());
            zip.Entries.Select(e => e.FullName).ShouldBe(["khach-hang.xlsx", "kho-kien-thuc.xlsx"], ignoreOrder: true);
        }

        var purge = Url($"/admin/tenants/{tenantId}/purge");
        await (await admin.PostAsJsonAsync(purge, new PurgeTenantRequest("Ngừng thuê"))).ShouldBeProblemAsync(409, "tenant_active");
        (await admin.PostAsync(Url($"/admin/tenants/{tenantId}/suspend"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PostAsJsonAsync(purge, new PurgeTenantRequest("Sai tên"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync(purge, new PurgeTenantRequest("Ngừng thuê"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await admin.GetAsync(Url($"/admin/tenants/{tenantId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var system = db.CreateDbContext(tenantId: null);
        (await system.Contacts.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId)).ShouldBe(0);
        (await system.Users.CountAsync(u => u.Id == ownerId)).ShouldBe(0); // chủ chỉ thuộc DN này → tài khoản bị xóa
        (await system.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "tenant.deleted" && a.Target!.StartsWith(tenantId.ToString()))).ShouldBe(1);
        (await system.Tenants.IgnoreQueryFilters().CountAsync(t => t.Id == other)).ShouldBe(1);
    }

    private async Task SetExpiryAsync(Guid tenantId, DateTimeOffset expiresAt)
    {
        await using var context = db.CreateDbContext(tenantId);
        (await context.Tenants.SingleAsync()).ExpiresAt = expiresAt;
        await context.SaveChangesAsync();
    }
}
