using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.IntegrationTests.Infrastructure;

/// <summary>Cô lập tenant cho kho kiến thức (skill tenant-safe-feature bước 7), kể cả tìm kiếm vector.</summary>
[Collection(PostgresGroup.Name)]
public sealed class KnowledgeIsolationTests(PostgresFixture db)
{
    private const int Dim = 768;

    /// <summary>Vector đơn vị theo trục <paramref name="axis"/>: hai trục khác nhau = khoảng cách cosine 1.</summary>
    private static float[] Axis(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1f;
        return v;
    }

    private sealed record Scope(TenantContext Context, AppDbContext Db) : IAsyncDisposable
    {
        public KnowledgeItemRepository Items => new(Db, Context);

        public KnowledgeDocumentRepository Documents => new(Db, Context);

        public ChunkRepository Chunks => new(Db, Context);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Scope As(Guid tenantId)
    {
        var context = PostgresFixture.CreateTenantContext(tenantId);
        return new Scope(context, db.CreateDbContext(context));
    }

    private static KnowledgeItem NewItem(string code, string text) => new()
    {
        Kind = KnowledgeKind.Service,
        Code = code,
        DataJson = $$"""{"code":"{{code}}","name":"{{text}}"}""",
        SearchText = text,
        ContentHash = new string('a', 64),
    };

    /// <summary>Tạo 1 mục + 1 đoạn có embedding cho tenant.</summary>
    private async Task<KnowledgeItem> SeedItemWithChunkAsync(Guid tenantId, string code, string text, float[] embedding)
    {
        await using var s = As(tenantId);
        var item = s.Items.Add(tenantId, NewItem(code, text));
        s.Chunks.AddRange(tenantId, [new Chunk
        {
            KnowledgeItemId = item.Id,
            Content = text,
            MetaJson = $$"""{"code":"{{code}}"}""",
            Embedding = embedding,
            EmbeddingModel = "test",
        }]);
        await s.Db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task Same_code_allowed_in_different_tenants_but_not_twice_in_one()
    {
        var (tenantA, _) = await db.CreateTenantAsync("KB A");
        var (tenantB, _) = await db.CreateTenantAsync("KB B");

        await SeedItemWithChunkAsync(tenantA, "DV-MASSAGE-60", "Massage A", Axis(0));
        await SeedItemWithChunkAsync(tenantB, "DV-MASSAGE-60", "Massage B", Axis(1));

        await using var s = As(tenantA);
        s.Items.Add(tenantA, NewItem("DV-MASSAGE-60", "Trùng mã"));
        var ex = await Should.ThrowAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Tenant_B_cannot_list_or_load_tenant_A_items_and_documents()
    {
        var (tenantA, _) = await db.CreateTenantAsync("KB list A");
        var (tenantB, _) = await db.CreateTenantAsync("KB list B");
        await SeedItemWithChunkAsync(tenantA, "DV-RIENG-A", "Bí mật A", Axis(2));

        Guid documentA;
        await using (var s = As(tenantA))
        {
            documentA = s.Documents.Add(tenantA, new KnowledgeDocument
            {
                Title = "Bảng giá A",
                FileName = "bang-gia.pdf",
                ContentType = "application/pdf",
                StorageKey = "a/bang-gia.pdf",
                Sha256 = new string('b', 64),
            }).Id;
            await s.Db.SaveChangesAsync();
        }

        await using var asB = As(tenantB);
        (await asB.Items.ListAsync(tenantB, kind: null, CancellationToken.None)).ShouldBeEmpty();
        (await asB.Items.GetByCodesAsync(tenantB, ["DV-RIENG-A"], CancellationToken.None)).ShouldBeEmpty();
        (await asB.Documents.GetAsync(tenantB, documentA, CancellationToken.None)).ShouldBeNull();
        (await asB.Documents.FindByFileNameAsync(tenantB, "bang-gia.pdf", CancellationToken.None)).ShouldBeNull();
        (await asB.Db.Chunks.AnyAsync()).ShouldBeFalse();

        // Repository từ chối tenantId khác tenant hiện tại.
        await Should.ThrowAsync<TenantIsolationException>(() => asB.Items.ListAsync(tenantA, null, CancellationToken.None));
    }

    [Fact]
    public async Task Vector_search_only_returns_own_tenant_chunks_even_when_other_tenant_is_closer()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Search A");
        var (tenantB, _) = await db.CreateTenantAsync("Search B");

        // Đoạn của A trùng khớp tuyệt đối với câu hỏi; đoạn của B lệch hẳn.
        await SeedItemWithChunkAsync(tenantA, "DV-A", "Giá massage của A", Axis(10));
        await SeedItemWithChunkAsync(tenantB, "DV-B", "Giá massage của B", Axis(11));

        await using var asB = As(tenantB);
        var results = await asB.Chunks.SearchAsync(tenantB, Axis(10), k: 5, CancellationToken.None);

        results.ShouldHaveSingleItem().Content.ShouldBe("Giá massage của B");
        results.ShouldAllBe(r => r.Content != "Giá massage của A");

        await Should.ThrowAsync<TenantIsolationException>(() => asB.Chunks.SearchAsync(tenantA, Axis(10), 5, CancellationToken.None));
    }

    [Fact]
    public async Task Vector_search_orders_by_similarity()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Search order");
        await SeedItemWithChunkAsync(tenantId, "DV-XA", "Xa", Axis(21));
        await SeedItemWithChunkAsync(tenantId, "DV-GAN", "Gần", Axis(20));

        var query = Axis(20);
        query[21] = 0.2f;

        await using var s = As(tenantId);
        var results = await s.Chunks.SearchAsync(tenantId, query, k: 2, CancellationToken.None);

        results.Select(r => r.Content).ShouldBe(["Gần", "Xa"]);
        results[0].Distance.ShouldBeLessThan(results[1].Distance);
    }

    [Fact]
    public async Task Deleting_item_deletes_its_chunks()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Cascade");
        await SeedItemWithChunkAsync(tenantId, "DV-XOA", "Sẽ xóa", Axis(30));

        await using (var s = As(tenantId))
        {
            var item = (await s.Items.GetByCodesAsync(tenantId, ["DV-XOA"], CancellationToken.None)).ShouldHaveSingleItem();
            s.Items.Remove(tenantId, item);
            await s.Db.SaveChangesAsync();
        }

        await using var check = As(tenantId);
        (await check.Db.Chunks.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Chunk_must_have_exactly_one_source()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Chunk source");

        await using var s = As(tenantId);
        s.Chunks.AddRange(tenantId, [new Chunk { Content = "mồ côi", MetaJson = "{}" }]);

        var ex = await Should.ThrowAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Moving_item_to_another_tenant_is_blocked_by_DbContext()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Move A");
        var (tenantB, _) = await db.CreateTenantAsync("Move B");
        await SeedItemWithChunkAsync(tenantA, "DV-CHUYEN", "Chuyển", Axis(50));

        await using (var asA = As(tenantA))
        {
            var item = (await asA.Items.GetByCodesAsync(tenantA, ["DV-CHUYEN"], CancellationToken.None)).ShouldHaveSingleItem();
            item.TenantId = tenantB; // tenant_id không thuộc khóa chính → chính AppDbContext phải chặn

            await Should.ThrowAsync<TenantIsolationException>(() => asA.Db.SaveChangesAsync());
        }

        await using var asB = As(tenantB);
        (await asB.Items.ListAsync(tenantB, null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tenant_B_cannot_write_chunk_or_item_into_tenant_A()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Write A");
        var (tenantB, _) = await db.CreateTenantAsync("Write B");
        var itemA = await SeedItemWithChunkAsync(tenantA, "DV-A-WRITE", "A", Axis(40));

        await using var asB = As(tenantB);
        asB.Db.Chunks.Add(new Chunk { Id = Guid.CreateVersion7(), TenantId = tenantA, KnowledgeItemId = itemA.Id, Content = "chen", MetaJson = "{}" });

        await Should.ThrowAsync<TenantIsolationException>(() => asB.Db.SaveChangesAsync());
        Should.Throw<TenantIsolationException>(() => asB.Items.Remove(tenantB, itemA));
    }
}
