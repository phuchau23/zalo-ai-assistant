using System.Net;
using System.Text;
using ClosedXML.Excel;
using Shouldly;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

[Collection(PostgresGroup.Name)]
public sealed class KnowledgeTemplateEndpointsTests(PostgresFixture db)
{
    private async Task SeedItemsAsync(Guid tenantId, string json)
    {
        var entries = KnowledgeJsonReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json))).Entries;
        var context = PostgresFixture.CreateTenantContext(tenantId);
        await using var dbContext = db.CreateDbContext(context);
        var repository = new KnowledgeItemRepository(dbContext, context);
        foreach (var entry in entries)
        {
            repository.Add(tenantId, KnowledgeItemMapper.ToItem(entry, null));
        }

        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task Template_download_is_a_valid_empty_workbook()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Template");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using var response = await client.GetAsync(new Uri("/knowledge/template", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe("mau-du-lieu-kien-thuc.xlsx");
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        workbook.Worksheets.Count.ShouldBe(6);
    }

    [Fact]
    public async Task Export_contains_only_own_tenant_items_and_is_audited()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Export A");
        var (tenantB, _) = await db.CreateTenantAsync("Export B");
        await SeedItemsAsync(tenantA, """{ "services": [ { "code": "DV-CUA-A", "name": "Dịch vụ của A", "price": 100000 } ] }""");
        await SeedItemsAsync(tenantB, """{ "faqs": [ { "code": "FAQ-CUA-B", "question": "Hỏi B", "answer": "Đáp B" } ] }""");
        var staffB = await db.AddMemberAsync(tenantB, Core.Tenancy.TenantRole.Staff);
        using var client = await db.Api.CreateLoggedInClientAsync(staffB);

        using var json = await client.GetAsync(new Uri("/knowledge/export?format=json", UriKind.Relative));
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await json.Content.ReadAsStringAsync();
        body.ShouldContain("FAQ-CUA-B");
        body.ShouldNotContain("DV-CUA-A");

        using var xlsx = await client.GetAsync(new Uri("/knowledge/export", UriKind.Relative));
        var parsed = KnowledgeExcel.Read(await xlsx.Content.ReadAsStreamAsync());
        parsed.Entries.Select(e => e.Code).ShouldBe(["FAQ-CUA-B"]);

        await using var check = db.CreateDbContext(tenantB);
        check.AuditLogs.Count(a => a.TenantId == tenantB && a.Action == "knowledge.exported").ShouldBe(2);
    }

    [Fact]
    public async Task Export_rejects_unknown_format_and_requires_login()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Export format");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var anonymous = db.Api.CreateClient();

        using (var bad = await client.GetAsync(new Uri("/knowledge/export?format=pdf", UriKind.Relative)))
        {
            await bad.ShouldBeProblemAsync(400, "invalid_input");
        }

        using var noLogin = await anonymous.GetAsync(new Uri("/knowledge/template", UriKind.Relative));
        noLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
