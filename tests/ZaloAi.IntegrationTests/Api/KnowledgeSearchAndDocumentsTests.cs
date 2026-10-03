using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Knowledge;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Đánh chỉ mục + tìm kiếm + tài liệu tự do (embedding giả, không gọi Gemini thật).</summary>
[Collection(PostgresGroup.Name)]
public sealed class KnowledgeSearchAndDocumentsTests(PostgresFixture db)
{
    private const string Data = """
        {
          "info": [ { "code": "TT-CN-THU-DUC", "title": "Chi nhánh Thủ Đức", "content": "Căn T16-21 Manhattan, Vinhomes Grand Park, Thủ Đức" } ],
          "services": [
            { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "price": 450000 },
            { "code": "DV-GOI-DAU", "name": "Gội đầu dưỡng sinh", "price": 150000 }
          ],
          "faqs": [ { "code": "FAQ-CON-BU", "question": "Đang cho con bú có massage được không?", "answer": "Được, dùng gói Mẹ Khỏe Mẹ Vui." } ]
        }
        """;

    private static readonly Uri Search = new("/knowledge/search", UriKind.Relative);
    private static readonly Uri Documents = new("/knowledge/documents", UriKind.Relative);

    /// <summary>Chạy job như Worker (Hangfire tạo scope riêng cho mỗi job).</summary>
    private async Task RunJobAsync<TJob>(Func<TJob, Task> run)
        where TJob : notnull
    {
        using var scope = db.Api.Services.CreateScope();
        await run(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    private async Task<HttpClient> TenantWithDataAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Data)), "file", "data.json" } };
        using var upload = await client.PostAsync(new Uri("/knowledge/imports", UriKind.Relative), form);
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        using var apply = await client.PostAsJsonAsync(
            new Uri($"/knowledge/imports/{preview.Id}/apply", UriKind.Relative),
            new { selections = preview.Items.Select(i => new { key = i.Key }) });
        apply.EnsureSuccessStatusCode();

        await RunJobAsync<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        return client;
    }

    private static Task<HttpResponseMessage> UploadDocumentAsync(HttpClient client, string fileName, byte[] content, bool replace = false)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return client.PostAsync(new Uri($"/knowledge/documents?replace={replace.ToString().ToLowerInvariant()}", UriKind.Relative), form);
    }

    [Fact]
    public async Task Applied_items_are_indexed_and_search_finds_the_right_one()
    {
        using var client = await TenantWithDataAsync("Search ok");

        var status = await client.GetFromJsonAsync<KnowledgeStatusResponse>(new Uri("/knowledge/status", UriKind.Relative));
        status.ShouldBe(new KnowledgeStatusResponse(Items: 4, Documents: 0, Chunks: 4, ChunksPendingIndex: 0, DocumentsProcessing: 0));

        using var response = await client.PostAsJsonAsync(Search, new { query = "giá massage đông y 60 phút", limit = 3 });
        var results = (await response.Content.ReadFromJsonAsync<List<KnowledgeSearchResult>>())!;

        results.Count.ShouldBe(3);
        results[0].Code.ShouldBe("DV-MASSAGE-60");
        results[0].Source.ShouldBe("item");
        results[0].Content.ShouldContain("450.000 đ");
        results.Select(r => r.Score).ShouldBeInOrder(SortDirection.Descending);

        using var branch = await client.PostAsJsonAsync(Search, new { query = "chi nhánh Thủ Đức ở đâu" });
        (await branch.Content.ReadFromJsonAsync<List<KnowledgeSearchResult>>())![0].Code.ShouldBe("TT-CN-THU-DUC");
    }

    [Fact]
    public async Task Search_never_returns_another_tenants_data()
    {
        using var clientA = await TenantWithDataAsync("Search iso A");
        var (tenantB, _) = await db.CreateTenantAsync("Search iso B");
        using var clientB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        using var response = await clientB.PostAsJsonAsync(Search, new { query = "giá massage đông y 60 phút" });

        (await response.Content.ReadFromJsonAsync<List<KnowledgeSearchResult>>())!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_validates_input()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Search validate");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using var empty = await client.PostAsJsonAsync(Search, new { query = "" });
        await empty.ShouldBeProblemAsync(400, "validation_failed");
        using var tooMany = await client.PostAsJsonAsync(Search, new { query = "x", limit = 100 });
        await tooMany.ShouldBeProblemAsync(400, "validation_failed");
    }

    [Fact]
    public async Task Document_upload_is_ingested_indexed_and_searchable()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Docs ok");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        var markdown = "# Trị liệu cổ vai gáy\n\nQuy trình 60 phút: chẩn đoán, bấm huyệt Phong Trì Kiên Tỉnh, massage phản xạ cột sống.\n\n# Bảng giá\n\n60 phút 550.000đ, 90 phút 750.000đ.";
        using var upload = await UploadDocumentAsync(client, "Trị liệu cổ vai gáy.md", Encoding.UTF8.GetBytes(markdown));
        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
        var document = (await upload.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>())!;
        document.Status.ShouldBe("pending");
        document.FileName.ShouldBe("Trị liệu cổ vai gáy.md");

        await RunJobAsync<IngestDocumentJob>(job => job.RunAsync(tenantId, document.Id, CancellationToken.None));

        var list = (await client.GetFromJsonAsync<List<KnowledgeDocumentResponse>>(Documents))!;
        var ready = list.ShouldHaveSingleItem();
        ready.Status.ShouldBe("ready");
        ready.ChunkCount.ShouldBe(2);

        using var search = await client.PostAsJsonAsync(Search, new { query = "huyệt Phong Trì cổ vai gáy" });
        var hit = (await search.Content.ReadFromJsonAsync<List<KnowledgeSearchResult>>())![0];
        hit.Source.ShouldBe("document");
        hit.DocumentId.ShouldBe(document.Id);
        hit.Title.ShouldBe("Trị liệu cổ vai gáy — Trị liệu cổ vai gáy");
    }

    [Fact]
    public async Task Same_file_name_requires_replace_and_replace_reprocesses()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Docs replace");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var v1 = (await (await UploadDocumentAsync(client, "gia.txt", Encoding.UTF8.GetBytes("Bảng giá cũ: massage 450.000đ một lượt."))).Content
            .ReadFromJsonAsync<KnowledgeDocumentResponse>())!;
        await RunJobAsync<IngestDocumentJob>(job => job.RunAsync(tenantId, v1.Id, CancellationToken.None));

        using (var again = await UploadDocumentAsync(client, "gia.txt", Encoding.UTF8.GetBytes("Bảng giá mới: massage 490.000đ một lượt.")))
        {
            await again.ShouldBeProblemAsync(409, "document_exists");
        }

        using var replaced = await UploadDocumentAsync(client, "gia.txt", Encoding.UTF8.GetBytes("Bảng giá mới: massage 490.000đ một lượt."), replace: true);
        var v2 = (await replaced.Content.ReadFromJsonAsync<KnowledgeDocumentResponse>())!;
        v2.Id.ShouldBe(v1.Id);
        v2.Status.ShouldBe("pending");
        await RunJobAsync<IngestDocumentJob>(job => job.RunAsync(tenantId, v2.Id, CancellationToken.None));

        await using var check = db.CreateDbContext(tenantId);
        var chunk = await check.Chunks.SingleAsync();
        chunk.Content.ShouldContain("490.000đ");
    }

    [Fact]
    public async Task Bad_files_are_rejected_or_marked_failed()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Docs bad");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        using (var exe = await UploadDocumentAsync(client, "virus.exe", [0x4D, 0x5A]))
        {
            await exe.ShouldBeProblemAsync(400, "invalid_input");
        }

        using (var fakePdf = await UploadDocumentAsync(client, "gia.pdf", Encoding.UTF8.GetBytes("không phải pdf")))
        {
            await fakePdf.ShouldBeProblemAsync(400, "invalid_input");
        }

        var broken = (await (await UploadDocumentAsync(client, "hong.pdf", Encoding.UTF8.GetBytes("%PDF-1.7 hỏng"))).Content
            .ReadFromJsonAsync<KnowledgeDocumentResponse>())!;
        await RunJobAsync<IngestDocumentJob>(job => job.RunAsync(tenantId, broken.Id, CancellationToken.None));

        var failed = (await client.GetFromJsonAsync<List<KnowledgeDocumentResponse>>(Documents))!.ShouldHaveSingleItem();
        failed.Status.ShouldBe("failed");
        failed.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Delete_document_removes_chunks_and_other_tenant_gets_404()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Docs del A");
        var (tenantB, _) = await db.CreateTenantAsync("Docs del B");
        using var ownerA = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantA));
        using var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        var document = (await (await UploadDocumentAsync(ownerA, "a.md", Encoding.UTF8.GetBytes("Nội dung riêng của doanh nghiệp A, không ai khác được thấy."))).Content
            .ReadFromJsonAsync<KnowledgeDocumentResponse>())!;
        await RunJobAsync<IngestDocumentJob>(job => job.RunAsync(tenantA, document.Id, CancellationToken.None));

        (await ownerB.GetFromJsonAsync<List<KnowledgeDocumentResponse>>(Documents))!.ShouldBeEmpty();
        using (var deleteByB = await ownerB.DeleteAsync(new Uri($"/knowledge/documents/{document.Id}", UriKind.Relative)))
        {
            await deleteByB.ShouldBeProblemAsync(404, "not_found");
        }

        using var staffA = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantA, TenantRole.Staff));
        using (var uploadByStaff = await UploadDocumentAsync(staffA, "x.md", Encoding.UTF8.GetBytes("nội dung đủ dài để đọc được")))
        {
            await uploadByStaff.ShouldBeProblemAsync(403, "forbidden");
        }

        using (var delete = await ownerA.DeleteAsync(new Uri($"/knowledge/documents/{document.Id}", UriKind.Relative)))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await using var check = db.CreateDbContext(tenantA);
        (await check.Documents.AnyAsync()).ShouldBeFalse();
        (await check.Chunks.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Ai_prompt_is_available_and_matches_template()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Prompt");
        using var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        var prompt = await client.GetFromJsonAsync<KnowledgeAiPromptResponse>(new Uri("/knowledge/ai-prompt", UriKind.Relative));

        prompt!.Prompt.ShouldContain("\"format\": \"zaloai-knowledge\"");
        prompt.Prompt.ShouldContain("KHÔNG tự đoán giá");
    }

    [Fact]
    public async Task Sweep_job_requeues_tenants_with_pending_chunks()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Sweep");
        await using (var dbA = db.CreateDbContext(tenantId))
        {
            var item = new KnowledgeItem { Id = Guid.CreateVersion7(), TenantId = tenantId, Kind = KnowledgeKind.Faq, Code = "FAQ-X", DataJson = "{}", SearchText = "x", ContentHash = new string('a', 64) };
            dbA.KnowledgeItems.Add(item);
            dbA.Chunks.Add(new Chunk { Id = Guid.CreateVersion7(), TenantId = tenantId, KnowledgeItemId = item.Id, Content = "chờ", MetaJson = "{}" });
            await dbA.SaveChangesAsync();
        }

        await RunJobAsync<IndexSweepJob>(job => job.RunAsync(CancellationToken.None)); // chỉ xếp job, không lỗi
        await RunJobAsync<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));

        await using var check = db.CreateDbContext(tenantId);
        (await check.Chunks.CountAsync(c => c.Embedding == null)).ShouldBe(0);
        (await check.Chunks.SingleAsync()).EmbeddingModel.ShouldBe("fake-bag-of-words");
    }
}
