using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Api.Knowledge;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Nhập file kiểu review pull request: tình huống giống dữ liệu mẫu v1 → v2, thu nhỏ.</summary>
[Collection(PostgresGroup.Name)]
public sealed class KnowledgeImportEndpointsTests(PostgresFixture db)
{
    private const string V1 = """
        {
          "format": "zaloai-knowledge", "version": 1,
          "info": [ { "code": "TT-GIO-MO-CUA", "title": "Giờ mở cửa", "content": "8:00 - 21:00" } ],
          "services": [
            { "code": "DV-MASSAGE-DONG-Y-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 450000 },
            { "code": "DV-MASSAGE-DONG-Y-90", "name": "Massage Đông y 90 phút", "durationMinutes": 90, "price": 550000 },
            { "code": "DV-XONG-HOI-THAO-DUOC", "name": "Xông hơi thảo dược", "price": 120000 }
          ]
        }
        """;

    private const string V2 = """
        {
          "format": "zaloai-knowledge", "version": 1,
          "info": [ { "code": "TT-GIO-MO-CUA", "title": "Giờ mở cửa", "content": "8:00 - 21:30" } ],
          "services": [
            { "code": "DV-MASSAGE-DONG-Y-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 490000 },
            { "code": "DV-MASSAGE-DY-90P", "name": "Massage Đông Y 90 phút", "durationMinutes": 90, "price": 590000 },
            { "code": "DV-NGAM-CHAN-THAO-DUOC", "name": "Ngâm chân thảo dược", "price": 99000 }
          ]
        }
        """;

    private static readonly Uri Imports = new("/knowledge/imports", UriKind.Relative);

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string content, string fileName = "du-lieu.json")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", fileName);
        return client.PostAsync(Imports, form);
    }

    private static async Task<KnowledgeImportResponse> PreviewAsync(HttpClient client, string content)
    {
        using var response = await UploadAsync(client, content);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, Guid importId, params object[] selections) =>
        client.PostAsJsonAsync(new Uri($"/knowledge/imports/{importId}/apply", UriKind.Relative), new { selections });

    private static async Task ApplyAllDefaultsAsync(HttpClient client, KnowledgeImportResponse preview)
    {
        using var response = await ApplyAsync(client, preview.Id, preview.Items.Where(i => i.DefaultSelected).Select(i => new { key = i.Key }).ToArray<object>());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task First_import_adds_everything_and_creates_pending_chunks()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import v1");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        var preview = await PreviewAsync(client, V1);
        preview.Status.ShouldBe("preview");
        preview.Summary.ShouldBe(new KnowledgeDiffSummaryResponse(4, 0, 0, 0, 0));
        preview.Items.ShouldAllBe(i => i.Type == "added" && i.DefaultSelected);

        // Bản xem trước chưa đổi dữ liệu.
        (await client.GetFromJsonAsync<List<KnowledgeItemResponse>>(new Uri("/knowledge/items", UriKind.Relative)))!.ShouldBeEmpty();

        await ApplyAllDefaultsAsync(client, preview);

        var items = (await client.GetFromJsonAsync<List<KnowledgeItemResponse>>(new Uri("/knowledge/items", UriKind.Relative)))!;
        items.Count.ShouldBe(4);
        items.Single(i => i.Code == "DV-MASSAGE-DONG-Y-60").Fields.ShouldContain(f => f.Field == "price" && f.Value == "450000");

        await using var check = db.CreateDbContext(tenantId);
        (await check.Chunks.CountAsync(c => c.Embedding == null)).ShouldBe(4); // chờ đánh chỉ mục (bước 5)
        (await check.KnowledgeImports.SingleAsync()).Status.ShouldBe(Core.Entities.KnowledgeImportStatus.Applied);
        (await check.AuditLogs.CountAsync(a => a.TenantId == tenantId && a.Action == "knowledge.import_applied")).ShouldBe(1);
    }

    [Fact]
    public async Task Second_import_shows_diff_and_applies_only_selected_with_merge()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import v2");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ApplyAllDefaultsAsync(client, await PreviewAsync(client, V1));

        var preview = await PreviewAsync(client, V2);

        preview.Summary.ShouldBe(new KnowledgeDiffSummaryResponse(Added: 1, Changed: 2, PossibleDuplicate: 1, Missing: 2, Unchanged: 0));
        var duplicate = preview.Items.Single(i => i.Type == "possibleDuplicate");
        duplicate.ExistingCode.ShouldBe("DV-MASSAGE-DONG-Y-90");
        preview.Items.Single(i => i.Code == "DV-MASSAGE-DONG-Y-60" && i.Type == "changed")
            .Changes.ShouldHaveSingleItem().ShouldBe(new KnowledgeFieldChangeResponse("price", "Giá (VNĐ)", "money", "450000", "490000"));

        // Chọn: đổi giá 60', gộp mục trùng vào mục cũ, thêm ngâm chân; KHÔNG đổi giờ mở cửa, KHÔNG xóa xông hơi.
        using (var apply = await ApplyAsync(client, preview.Id,
            new { key = "DV-MASSAGE-DONG-Y-60" },
            new { key = "DV-MASSAGE-DY-90P", resolution = "merge" },
            new { key = "DV-NGAM-CHAN-THAO-DUOC" }))
        {
            apply.StatusCode.ShouldBe(HttpStatusCode.OK, await apply.Content.ReadAsStringAsync());
            (await apply.Content.ReadFromJsonAsync<KnowledgeApplyResponse>()).ShouldBe(new KnowledgeApplyResponse(1, 1, 1, 0));
        }

        var items = (await client.GetFromJsonAsync<List<KnowledgeItemResponse>>(new Uri("/knowledge/items", UriKind.Relative)))!
            .ToDictionary(i => i.Code);
        items.Keys.Order().ShouldBe(["DV-MASSAGE-DONG-Y-60", "DV-MASSAGE-DONG-Y-90", "DV-NGAM-CHAN-THAO-DUOC", "DV-XONG-HOI-THAO-DUOC", "TT-GIO-MO-CUA"]);
        items["DV-MASSAGE-DONG-Y-60"].Fields.ShouldContain(f => f.Field == "price" && f.Value == "490000");
        items["DV-MASSAGE-DONG-Y-90"].Fields.ShouldContain(f => f.Field == "price" && f.Value == "590000"); // đã gộp, giữ mã cũ
        items["DV-MASSAGE-DONG-Y-90"].Title.ShouldBe("Massage Đông Y 90 phút");
        items["TT-GIO-MO-CUA"].Fields.ShouldContain(f => f.Value == "8:00 - 21:00"); // không chọn → giữ nguyên

        var detail = await client.GetFromJsonAsync<KnowledgeImportResponse>(new Uri($"/knowledge/imports/{preview.Id}", UriKind.Relative));
        detail!.Status.ShouldBe("applied");
        detail.SelectedKeys.ShouldBe(["DV-MASSAGE-DONG-Y-60", "DV-MASSAGE-DY-90P", "DV-NGAM-CHAN-THAO-DUOC"]);

        // Mục gộp/đổi có đoạn mới chờ đánh chỉ mục; mục không đổi giữ đoạn cũ.
        await using var check = db.CreateDbContext(tenantId);
        (await check.Chunks.CountAsync()).ShouldBe(5);
    }

    [Fact]
    public async Task Missing_item_is_deleted_only_when_selected()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import delete");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ApplyAllDefaultsAsync(client, await PreviewAsync(client, V1));

        var preview = await PreviewAsync(client, V2);
        using (var apply = await ApplyAsync(client, preview.Id, new { key = "missing:DV-XONG-HOI-THAO-DUOC" }))
        {
            (await apply.Content.ReadFromJsonAsync<KnowledgeApplyResponse>()).ShouldBe(new KnowledgeApplyResponse(0, 0, 0, 1));
        }

        await using var check = db.CreateDbContext(tenantId);
        (await check.KnowledgeItems.AnyAsync(i => i.Code == "DV-XONG-HOI-THAO-DUOC")).ShouldBeFalse();
        (await check.KnowledgeItems.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Cannot_merge_into_and_delete_the_same_item()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import conflict");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ApplyAllDefaultsAsync(client, await PreviewAsync(client, V1));
        var preview = await PreviewAsync(client, V2);

        using var apply = await ApplyAsync(client, preview.Id,
            new { key = "DV-MASSAGE-DY-90P", resolution = "merge" },
            new { key = "missing:DV-MASSAGE-DONG-Y-90" });

        await apply.ShouldBeProblemAsync(400, "invalid_input");
    }

    [Fact]
    public async Task Possible_duplicate_requires_a_resolution()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import dup");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ApplyAllDefaultsAsync(client, await PreviewAsync(client, V1));
        var preview = await PreviewAsync(client, V2);

        using var apply = await ApplyAsync(client, preview.Id, new { key = "DV-MASSAGE-DY-90P" });

        await apply.ShouldBeProblemAsync(400, "invalid_input");
    }

    [Fact]
    public async Task Stale_preview_is_rejected_when_items_changed_meanwhile()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import stale");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await ApplyAllDefaultsAsync(client, await PreviewAsync(client, V1));

        var older = await PreviewAsync(client, V2);
        var newer = await PreviewAsync(client, V2);
        await ApplyAllDefaultsAsync(client, newer); // đổi giá 60' trước

        using var apply = await ApplyAsync(client, older.Id, new { key = "DV-MASSAGE-DONG-Y-60" });
        await apply.ShouldBeProblemAsync(409, "conflict");
    }

    [Fact]
    public async Task Applied_or_discarded_import_cannot_be_applied_again()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import twice");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var applied = await PreviewAsync(client, V1);
        await ApplyAllDefaultsAsync(client, applied);

        using (var again = await ApplyAsync(client, applied.Id, new { key = "TT-GIO-MO-CUA" }))
        {
            await again.ShouldBeProblemAsync(409, "conflict");
        }

        var discarded = await PreviewAsync(client, V2);
        using (var discard = await client.PostAsync(new Uri($"/knowledge/imports/{discarded.Id}/discard", UriKind.Relative), null))
        {
            discard.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var afterDiscard = await ApplyAsync(client, discarded.Id, new { key = "DV-NGAM-CHAN-THAO-DUOC" });
        await afterDiscard.ShouldBeProblemAsync(409, "conflict");

        var history = await client.GetFromJsonAsync<List<KnowledgeImportSummaryResponse>>(Imports);
        history!.Select(h => h.Status).ShouldBe(["discarded", "applied"]);
    }

    [Fact]
    public async Task Invalid_file_returns_located_errors_and_creates_nothing()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Import invalid");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using var response = await UploadAsync(client, """{ "services": [ { "code": "dv sai", "name": "X", "price": "liên hệ" } ] }""");

        await response.ShouldBeProblemAsync(400, "invalid_file");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = json.RootElement.GetProperty("fileErrors").EnumerateArray().ToList();
        errors.Count.ShouldBe(2);
        errors.ShouldAllBe(e => e.GetProperty("location").GetString() == "Dịch vụ" && e.GetProperty("row").GetInt32() == 1);

        using var wrongType = await UploadAsync(client, "%PDF", "bang-gia.pdf");
        await wrongType.ShouldBeProblemAsync(400, "invalid_input");

        await using var check = db.CreateDbContext(tenantId);
        (await check.KnowledgeImports.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Staff_can_view_but_not_import_and_other_tenant_gets_404()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Import perm A");
        var (tenantB, _) = await db.CreateTenantAsync("Import perm B");
        using var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        var preview = await PreviewAsync(ownerA, V1);

        using var staffA = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantA, TenantRole.Staff));
        (await staffA.GetFromJsonAsync<KnowledgeImportResponse>(new Uri($"/knowledge/imports/{preview.Id}", UriKind.Relative)))!.Id.ShouldBe(preview.Id);
        using (var upload = await UploadAsync(staffA, V1))
        {
            await upload.ShouldBeProblemAsync(403, "forbidden");
        }

        using (var applyByStaff = await ApplyAsync(staffA, preview.Id, new { key = "TT-GIO-MO-CUA" }))
        {
            await applyByStaff.ShouldBeProblemAsync(403, "forbidden");
        }

        using var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        using (var get = await ownerB.GetAsync(new Uri($"/knowledge/imports/{preview.Id}", UriKind.Relative)))
        {
            await get.ShouldBeProblemAsync(404, "not_found");
        }

        using (var apply = await ApplyAsync(ownerB, preview.Id, new { key = "TT-GIO-MO-CUA" }))
        {
            await apply.ShouldBeProblemAsync(404, "not_found");
        }

        (await ownerB.GetFromJsonAsync<List<KnowledgeImportSummaryResponse>>(Imports))!.ShouldBeEmpty();
    }
}
