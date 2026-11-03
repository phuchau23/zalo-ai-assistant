using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Api.Tenants;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Nhân viên của doanh nghiệp + đổi mật khẩu: chỉ chủ quản lý, chốt người chủ cuối cùng, phiên cũ bị đăng xuất, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class TeamTests(PostgresFixture db)
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = db.Api.CreateClient();
        (await client.LoginAsync(email, password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return client;
    }

    [Fact]
    public async Task Owner_adds_staff_who_logs_in_with_temporary_password_and_has_staff_rights_only()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Đội ngũ");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var email = $"nv-{Guid.NewGuid():N}@test.local";

        using var add = await owner.PostAsJsonAsync(Url("/tenant/team"), new AddTeamMemberRequest(email, "Lan", "staff"));
        add.StatusCode.ShouldBe(HttpStatusCode.OK);
        var added = (await add.Content.ReadFromJsonAsync<AddTeamMemberResponse>())!;
        added.TemporaryPassword.ShouldNotBeNullOrEmpty();
        await (await owner.PostAsJsonAsync(Url("/tenant/team"), new AddTeamMemberRequest(email, "Lan", "staff"))).ShouldBeProblemAsync(409, "already_member");

        var staff = await LoginAsync(email, added.TemporaryPassword!);
        (await staff.GetAsync(Url("/inbox/conversations"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.GetAsync(Url("/tenant/team"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.GetAsync(Url("/contacts/export"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var team = (await owner.GetFromJsonAsync<List<TeamMemberResponse>>(Url("/tenant/team")))!;
        team.Count.ShouldBe(2);
        team.Single(m => m.IsYou).Role.ShouldBe("owner");

        // Lên làm chủ → có quyền chủ ngay ở request kế tiếp.
        (await owner.PatchAsJsonAsync(Url($"/tenant/team/{added.Member.UserId}"), new ChangeTeamRoleRequest("owner"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await staff.GetAsync(Url("/tenant/team"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Last_owner_cannot_be_demoted_or_removed_and_owner_cannot_remove_self()
    {
        var (tenantId, ownerId) = await db.CreateTenantAsync("Một chủ");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        await (await owner.PatchAsJsonAsync(Url($"/tenant/team/{ownerId}"), new ChangeTeamRoleRequest("staff"))).ShouldBeProblemAsync(409, "last_owner");
        await (await owner.DeleteAsync(Url($"/tenant/team/{ownerId}"))).ShouldBeProblemAsync(409, "cannot_remove_self");
        await (await owner.PostAsync(Url($"/tenant/team/{ownerId}/reset-password"), null)).ShouldBeProblemAsync(409, "use_change_password");
    }

    [Fact]
    public async Task Removed_member_loses_access_immediately_and_lonely_account_is_deleted()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Xóa nhân viên");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var email = $"nv-{Guid.NewGuid():N}@test.local";
        var added = (await (await owner.PostAsJsonAsync(Url("/tenant/team"), new AddTeamMemberRequest(email, "Hùng", "staff")))
            .Content.ReadFromJsonAsync<AddTeamMemberResponse>())!;
        var staff = await LoginAsync(email, added.TemporaryPassword!);

        (await owner.DeleteAsync(Url($"/tenant/team/{added.Member.UserId}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await staff.GetAsync(Url("/inbox/conversations"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // tài khoản đã bị xóa
        await using var context = db.CreateDbContext(tenantId: null);
        (await context.Users.CountAsync(u => u.Id == added.Member.UserId)).ShouldBe(0);
    }

    [Fact]
    public async Task Reset_password_logs_out_old_sessions_but_not_for_accounts_shared_with_another_business()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Cấp lại A");
        var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        var email = $"nv-{Guid.NewGuid():N}@test.local";
        var added = (await (await ownerA.PostAsJsonAsync(Url("/tenant/team"), new AddTeamMemberRequest(email, "Mai", "staff")))
            .Content.ReadFromJsonAsync<AddTeamMemberResponse>())!;
        var oldSession = await LoginAsync(email, added.TemporaryPassword!);

        using var reset = await ownerA.PostAsync(Url($"/tenant/team/{added.Member.UserId}/reset-password"), null);
        var fresh = (await reset.Content.ReadFromJsonAsync<ResetPasswordResponse>())!.TemporaryPassword;
        (await oldSession.GetAsync(Url("/auth/me"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await db.Api.CreateClient().LoginAsync(email, added.TemporaryPassword!)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await LoginAsync(email, fresh);

        // Cùng người được DN B thêm (email đã có tài khoản → không cấp mật khẩu); A không được cấp lại mật khẩu nữa, B cũng không.
        var (tenantB, _) = await db.CreateTenantAsync("Cấp lại B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        var shared = (await (await ownerB.PostAsJsonAsync(Url("/tenant/team"), new AddTeamMemberRequest(email, "Mai", "staff")))
            .Content.ReadFromJsonAsync<AddTeamMemberResponse>())!;
        shared.TemporaryPassword.ShouldBeNull();
        await (await ownerA.PostAsync(Url($"/tenant/team/{added.Member.UserId}/reset-password"), null)).ShouldBeProblemAsync(409, "shared_account");
        await (await ownerB.PostAsync(Url($"/tenant/team/{added.Member.UserId}/reset-password"), null)).ShouldBeProblemAsync(409, "shared_account");
    }

    [Fact]
    public async Task Owner_of_another_business_cannot_touch_members()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Cô lập A");
        var staffEmail = await db.AddMemberAsync(tenantA, TenantRole.Staff);
        var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        var staffId = (await ownerA.GetFromJsonAsync<List<TeamMemberResponse>>(Url("/tenant/team")))!.Single(m => m.Email == staffEmail).UserId;

        var (tenantB, _) = await db.CreateTenantAsync("Cô lập B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        (await ownerB.GetFromJsonAsync<List<TeamMemberResponse>>(Url("/tenant/team")))!.ShouldAllBe(m => m.UserId != staffId);
        (await ownerB.PatchAsJsonAsync(Url($"/tenant/team/{staffId}"), new ChangeTeamRoleRequest("owner"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.DeleteAsync(Url($"/tenant/team/{staffId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsync(Url($"/tenant/team/{staffId}/reset-password"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Change_password_keeps_this_session_and_logs_out_others()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Đổi mật khẩu");
        var email = PostgresFixture.OwnerEmail(tenantId);
        var here = await db.Api.CreateLoggedInClientAsync(email);
        var elsewhere = await db.Api.CreateLoggedInClientAsync(email);

        (await here.PostAsJsonAsync(Url("/auth/change-password"), new { currentPassword = "sai-mat-khau", newPassword = "MatKhauMoi@1" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await here.PostAsJsonAsync(Url("/auth/change-password"), new { currentPassword = PostgresFixture.TestPassword, newPassword = "ngan" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await here.PostAsJsonAsync(Url("/auth/change-password"), new { currentPassword = PostgresFixture.TestPassword, newPassword = "MatKhauMoi@1" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await here.GetAsync(Url("/auth/me"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await elsewhere.GetAsync(Url("/auth/me"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await LoginAsync(email, "MatKhauMoi@1");
    }
}
