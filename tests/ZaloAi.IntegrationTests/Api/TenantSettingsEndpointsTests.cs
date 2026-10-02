using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Api.Tenants;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

[Collection(PostgresGroup.Name)]
public sealed class TenantSettingsEndpointsTests(PostgresFixture db)
{
    private static readonly Uri Settings = new("/tenant/settings", UriKind.Relative);

    private static object ValidUpdate(string name = "Khoa Học Nguyệt Đạo") => new
    {
        name,
        industrySlug = "spa",
        botName = "Nguyệt",
        botPronoun = "em",
        privacyUrl = "https://nguyetdao.vn/chinh-sach-bao-mat",
    };

    [Fact]
    public async Task Owner_updates_settings_and_change_is_audited()
    {
        var (tenantId, ownerId) = await db.CreateTenantAsync("Tên cũ");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using var response = await client.PutAsJsonAsync(Settings, ValidUpdate());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = (await client.GetFromJsonAsync<TenantSettingsResponse>(Settings)).ShouldNotBeNull();
        settings.Name.ShouldBe("Khoa Học Nguyệt Đạo");
        settings.BotName.ShouldBe("Nguyệt");
        settings.PrivacyUrl.ShouldBe("https://nguyetdao.vn/chinh-sach-bao-mat");

        await using var asTenant = db.CreateDbContext(tenantId);
        (await asTenant.AuditLogs.CountAsync(a => a.UserId == ownerId && a.Action == "tenant.settings_updated")).ShouldBe(1);
    }

    [Fact]
    public async Task Staff_can_read_but_not_update_settings()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Spa Staff");
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        using var client = await db.Api.CreateLoggedInClientAsync(staffEmail);

        (await client.GetFromJsonAsync<TenantSettingsResponse>(Settings)).ShouldNotBeNull().Name.ShouldBe("Spa Staff");

        using var update = await client.PutAsJsonAsync(Settings, ValidUpdate("Bị đổi"));
        await update.ShouldBeProblemAsync(403, "forbidden");
    }

    [Fact]
    public async Task Each_tenant_only_sees_its_own_settings()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Tenant A riêng");
        var (tenantB, _) = await db.CreateTenantAsync("Tenant B riêng");
        using var clientA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        using var clientB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        using (await clientB.PutAsJsonAsync(Settings, ValidUpdate("B đã đổi"))) { }

        (await clientA.GetFromJsonAsync<TenantSettingsResponse>(Settings)).ShouldNotBeNull().Name.ShouldBe("Tenant A riêng");
        (await clientB.GetFromJsonAsync<TenantSettingsResponse>(Settings)).ShouldNotBeNull().Name.ShouldBe("B đã đổi");
    }

    [Fact]
    public async Task Removed_member_loses_access_immediately()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Spa Removed");
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        using var client = await db.Api.CreateLoggedInClientAsync(staffEmail);

        await db.RemoveMemberAsync(tenantId, staffEmail);

        using var response = await client.GetAsync(Settings);
        await response.ShouldBeProblemAsync(403, "no_active_tenant");
    }

    [Fact]
    public async Task Suspended_tenant_blocks_access()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Spa Suspended");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        await db.SetTenantStatusAsync(tenantId, TenantStatus.Suspended);

        using var response = await client.GetAsync(Settings);
        await response.ShouldBeProblemAsync(403, "no_active_tenant");
    }

    [Fact]
    public async Task Settings_without_login_returns_401()
    {
        using var client = db.Api.CreateClient();

        using var response = await client.GetAsync(Settings);

        await response.ShouldBeProblemAsync(401, "unauthenticated");
    }

    [Theory]
    [InlineData("", "spa", "https://a.vn", "name")]
    [InlineData("Tên", "khong-co-nganh", "https://a.vn", "industrySlug")]
    [InlineData("Tên", "spa", "http://khong-https.vn", "privacyUrl")]
    [InlineData("Tên", "spa", "javascript:alert(1)", "privacyUrl")]
    public async Task Invalid_settings_return_400_with_field_error(string name, string industrySlug, string privacyUrl, string field)
    {
        var (tenantId, _) = await db.CreateTenantAsync("Spa Invalid");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using var response = await client.PutAsJsonAsync(Settings, new { name, industrySlug, botName = "Bot", botPronoun = "em", privacyUrl });

        await response.ShouldBeProblemAsync(400, "validation_failed");
        (await response.Content.ReadAsStringAsync()).ShouldContain($"\"{field}\"");
    }
}
