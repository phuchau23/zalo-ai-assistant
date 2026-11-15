using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Shouldly;
using ZaloAi.Api.Inbox;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Tin nhắn mẫu (Z4): chủ DN quản lý, nhân viên dùng, phím tắt không trùng, DN khác không thấy / không sửa được.</summary>
[Collection(PostgresGroup.Name)]
public sealed class QuickReplyTests(PostgresFixture db)
{
    private static readonly Uri Base = new("/quick-replies", UriKind.Relative);

    [Fact]
    public async Task Owner_manages_templates_and_staff_only_reads_them()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Tin nhắn mẫu");
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, ZaloAi.Core.Tenancy.TenantRole.Staff));

        using var created = await owner.PostAsJsonAsync(Base, new { shortcut = "gia", title = "Bảng giá", content = "Dạ bảng giá bên em…", sortOrder = 1 });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reply = (await created.Content.ReadFromJsonAsync<QuickReplyResponse>())!;

        using (var duplicate = await owner.PostAsJsonAsync(Base, new { shortcut = "gia", title = "x", content = "y", sortOrder = 0 }))
        {
            await duplicate.ShouldBeProblemAsync(StatusCodes.Status409Conflict, "quick_reply_duplicate");
        }

        using (var badShortcut = await owner.PostAsJsonAsync(Base, new { shortcut = "Giá Tiền", title = "x", content = "y", sortOrder = 0 }))
        {
            badShortcut.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await staff.GetFromJsonAsync<List<QuickReplyResponse>>(Base))!.Single().Shortcut.ShouldBe("gia");
        (await staff.PostAsJsonAsync(Base, new { shortcut = "x", title = "x", content = "y", sortOrder = 0 })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using (var update = await owner.PutAsJsonAsync(new Uri($"/quick-replies/{reply.Id}", UriKind.Relative), new { shortcut = "bang-gia", title = "Bảng giá mới", content = "Dạ…", sortOrder = 2 }))
        {
            update.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var delete = await owner.DeleteAsync(new Uri($"/quick-replies/{reply.Id}", UriKind.Relative));
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<QuickReplyResponse>>(Base))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_business_cannot_see_or_touch_templates()
    {
        var (tenantA, _) = await db.CreateTenantAsync("DN A mẫu");
        var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        using var created = await ownerA.PostAsJsonAsync(Base, new { shortcut = "chao", title = "Chào", content = "Dạ em chào", sortOrder = 0 });
        var id = (await created.Content.ReadFromJsonAsync<QuickReplyResponse>())!.Id;

        var (tenantB, _) = await db.CreateTenantAsync("DN B mẫu");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        (await ownerB.GetFromJsonAsync<List<QuickReplyResponse>>(Base))!.ShouldBeEmpty();
        (await ownerB.PutAsJsonAsync(new Uri($"/quick-replies/{id}", UriKind.Relative), new { shortcut = "chao", title = "x", content = "y", sortOrder = 0 }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.DeleteAsync(new Uri($"/quick-replies/{id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Base, new { shortcut = "chao", title = "B", content = "B", sortOrder = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK); // phím tắt riêng từng DN
        (await ownerA.GetFromJsonAsync<List<QuickReplyResponse>>(Base))!.Single().Title.ShouldBe("Chào");
    }
}
