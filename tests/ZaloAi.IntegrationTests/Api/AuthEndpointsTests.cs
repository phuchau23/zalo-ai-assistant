using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Api.Auth;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

[Collection(PostgresGroup.Name)]
public sealed class AuthEndpointsTests(PostgresFixture db)
{
    private static readonly Uri Me = new("/auth/me", UriKind.Relative);

    private static async Task<string> WithoutTraceIdAsync(HttpResponseMessage response) =>
        System.Text.RegularExpressions.Regex.Replace(await response.Content.ReadAsStringAsync(), "\"traceId\":\"[^\"]*\"", "");

    [Fact]
    public async Task Login_sets_httponly_cookie_and_returns_current_tenant()
    {
        var (tenantId, ownerId) = await db.CreateTenantAsync("Spa Login");
        using var client = db.Api.CreateClient();

        using var response = await client.LoginAsync(PostgresFixture.OwnerEmail(tenantId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem();
        setCookie.ShouldContain("zaloai.session=");
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("samesite=lax", Case.Insensitive);

        var me = (await response.Content.ReadFromJsonAsync<MeResponse>()).ShouldNotBeNull();
        me.UserId.ShouldBe(ownerId);
        me.CurrentTenant.ShouldNotBeNull().Id.ShouldBe(tenantId);
        me.CurrentTenant.Role.ShouldBe("owner");

        (await client.GetFromJsonAsync<MeResponse>(Me)).ShouldNotBeNull().UserId.ShouldBe(ownerId);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_give_same_401()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Spa Wrong");
        using var client = db.Api.CreateClient();

        using var wrongPassword = await client.LoginAsync(PostgresFixture.OwnerEmail(tenantId), "sai-mat-khau");
        using var unknownEmail = await client.LoginAsync("khong-ton-tai@test.local", "sai-mat-khau");

        await wrongPassword.ShouldBeProblemAsync(401, "invalid_credentials");
        await unknownEmail.ShouldBeProblemAsync(401, "invalid_credentials");
        // Nội dung giống hệt (trừ traceId) để không đoán được email nào có tài khoản.
        (await WithoutTraceIdAsync(wrongPassword)).ShouldBe(await WithoutTraceIdAsync(unknownEmail));
        wrongPassword.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Login_attempts_are_audited()
    {
        var (tenantId, ownerId) = await db.CreateTenantAsync("Spa Audit");
        using var client = db.Api.CreateClient();

        using (await client.LoginAsync(PostgresFixture.OwnerEmail(tenantId), "sai")) { }
        using (await client.LoginAsync(PostgresFixture.OwnerEmail(tenantId))) { }

        await using var asTenant = db.CreateDbContext(tenantId);
        var actions = await asTenant.AuditLogs.Where(a => a.UserId == ownerId).OrderBy(a => a.CreatedAt).Select(a => a.Action).ToListAsync();
        actions.ShouldBe(["auth.login_failed", "auth.login"]);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("khong-phai-email", "x")]
    [InlineData("a@test.local", "")]
    public async Task Invalid_login_body_returns_400_with_field_errors(string email, string password)
    {
        using var client = db.Api.CreateClient();

        using var response = await client.LoginAsync(email, password);

        await response.ShouldBeProblemAsync(400, "validation_failed");
    }

    [Fact]
    public async Task Me_without_cookie_returns_401()
    {
        using var client = db.Api.CreateClient();

        using var response = await client.GetAsync(Me);

        await response.ShouldBeProblemAsync(401, "unauthenticated");
    }

    [Fact]
    public async Task Logout_clears_session()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Spa Logout");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using (var logout = await client.PostAsync(new Uri("/auth/logout", UriKind.Relative), null))
        {
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var me = await client.GetAsync(Me);
        me.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task User_in_two_tenants_can_switch_only_to_own_tenants()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Tenant A");
        var (tenantB, _) = await db.CreateTenantAsync("Tenant B");
        var (tenantC, _) = await db.CreateTenantAsync("Tenant C");
        var email = await db.AddMemberAsync(tenantA, Core.Tenancy.TenantRole.Staff);
        await db.AddMemberAsync(tenantB, Core.Tenancy.TenantRole.Owner, email);
        using var client = await db.Api.CreateLoggedInClientAsync(email);

        using var switched = await client.PostAsJsonAsync(new Uri("/auth/switch-tenant", UriKind.Relative), new { tenantId = tenantB });
        var me = (await switched.Content.ReadFromJsonAsync<MeResponse>()).ShouldNotBeNull();
        me.CurrentTenant.ShouldNotBeNull().Id.ShouldBe(tenantB);
        me.Tenants.Select(t => t.Id).ShouldBe([tenantA, tenantB], ignoreOrder: true);

        using var forbidden = await client.PostAsJsonAsync(new Uri("/auth/switch-tenant", UriKind.Relative), new { tenantId = tenantC });
        await forbidden.ShouldBeProblemAsync(404, "not_found");

        (await client.GetFromJsonAsync<MeResponse>(Me)).ShouldNotBeNull().CurrentTenant!.Id.ShouldBe(tenantB);
    }

    [Fact]
    public async Task Sixth_login_attempt_in_a_minute_is_rate_limited()
    {
        await using var api = new ApiFactory(db.ConnectionString, loginPermitPerMinute: 5);
        using var client = api.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var attempt = await client.LoginAsync("ai-do@test.local", "doan-mat-khau");
            attempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var blocked = await client.LoginAsync("ai-do@test.local", "doan-mat-khau");
        await blocked.ShouldBeProblemAsync(429, "rate_limited");
    }
}
