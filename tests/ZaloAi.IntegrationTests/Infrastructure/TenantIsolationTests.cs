using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.IntegrationTests.Infrastructure;

/// <summary>
/// Chứng minh tenant A không đọc/ghi được dữ liệu tenant B, ở cả 2 lớp: DbContext (global filter + SaveChanges)
/// và repository. Bắt buộc theo CLAUDE.md mục 8.
/// </summary>
[Collection(PostgresGroup.Name)]
public sealed class TenantIsolationTests(PostgresFixture db)
{
    [Fact]
    public async Task Tenant_B_cannot_read_tenant_A_rows_through_DbContext()
    {
        var (tenantA, _) = await db.CreateTenantAsync("A");
        var (tenantB, _) = await db.CreateTenantAsync("B");

        await using var asB = db.CreateDbContext(tenantB);

        (await asB.Tenants.AnyAsync(t => t.Id == tenantA)).ShouldBeFalse();
        (await asB.Memberships.AnyAsync(m => m.TenantId == tenantA)).ShouldBeFalse();
        (await asB.Tenants.Select(t => t.Id).ToListAsync()).ShouldBe([tenantB]);
    }

    [Fact]
    public async Task Context_without_tenant_sees_no_tenant_rows()
    {
        await db.CreateTenantAsync("A");

        await using var noTenant = db.CreateDbContext(tenantId: null);

        (await noTenant.Tenants.CountAsync()).ShouldBe(0);
        (await noTenant.Memberships.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Tenant_B_cannot_insert_row_for_tenant_A()
    {
        var (tenantA, ownerA) = await db.CreateTenantAsync("A");
        var (tenantB, _) = await db.CreateTenantAsync("B");

        await using var asB = db.CreateDbContext(tenantB);
        asB.Memberships.Add(new Membership { TenantId = tenantA, UserId = ownerA, Role = TenantRole.Staff });

        await Should.ThrowAsync<TenantIsolationException>(() => asB.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_B_cannot_update_or_delete_tenant_A_rows()
    {
        var (tenantA, ownerA) = await db.CreateTenantAsync("A");
        var (tenantB, _) = await db.CreateTenantAsync("B");

        // Giả lập code lỗi: có được entity của A (ví dụ từ cache) rồi attach vào context của B.
        await using (var asB = db.CreateDbContext(tenantB))
        {
            var stolen = new Tenant { Id = tenantA, Name = "hacked", IndustrySlug = "spa", BotName = "x", BotPronoun = "x" };
            asB.Tenants.Update(stolen);
            await Should.ThrowAsync<TenantIsolationException>(() => asB.SaveChangesAsync());
        }

        await using (var asB = db.CreateDbContext(tenantB))
        {
            asB.Memberships.Remove(new Membership { TenantId = tenantA, UserId = ownerA });
            await Should.ThrowAsync<TenantIsolationException>(() => asB.SaveChangesAsync());
        }

        await using var asA = db.CreateDbContext(tenantA);
        (await asA.Tenants.SingleAsync()).Name.ShouldBe("A");
        (await asA.Memberships.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Moving_a_row_to_another_tenant_is_blocked()
    {
        var (tenantA, ownerA) = await db.CreateTenantAsync("A");
        var (tenantB, _) = await db.CreateTenantAsync("B");

        await using (var asA = db.CreateDbContext(tenantA))
        {
            var membership = await asA.Memberships.SingleAsync(m => m.UserId == ownerA);
            membership.TenantId = tenantB;

            // memberships.tenant_id nằm trong khóa chính nên EF chặn trước (InvalidOperationException).
            // Bảng có tenant_id ngoài khóa (từ M2) sẽ bị AppDbContext chặn bằng TenantIsolationException.
            await Should.ThrowAsync<Exception>(() => asA.SaveChangesAsync());
        }

        await using var asB = db.CreateDbContext(tenantB);
        (await asB.Memberships.AnyAsync(m => m.UserId == ownerA)).ShouldBeFalse();
    }

    [Fact]
    public async Task Repository_rejects_tenantId_different_from_current_tenant()
    {
        var (tenantA, ownerA) = await db.CreateTenantAsync("A");
        var (tenantB, _) = await db.CreateTenantAsync("B");

        var contextB = PostgresFixture.CreateTenantContext(tenantB);
        await using var asB = db.CreateDbContext(contextB);
        var tenants = new TenantRepository(asB, contextB);
        var memberships = new MembershipRepository(asB, contextB);
        var audit = new AuditLogRepository(asB, contextB);

        await Should.ThrowAsync<TenantIsolationException>(() => tenants.GetAsync(tenantA, CancellationToken.None));
        await Should.ThrowAsync<TenantIsolationException>(() => memberships.GetAsync(tenantA, ownerA, CancellationToken.None));
        await Should.ThrowAsync<TenantIsolationException>(() => audit.ListAsync(tenantA, 10, CancellationToken.None));
        Should.Throw<TenantIsolationException>(() => audit.Add(tenantA, null, "test.action"));
    }

    [Fact]
    public async Task Audit_logs_are_listed_only_for_own_tenant()
    {
        var (tenantA, _) = await db.CreateTenantAsync("A");
        var (tenantB, _) = await db.CreateTenantAsync("B");

        foreach (var (tenant, action) in new[] { (tenantA, "a.secret_action"), (tenantB, "b.action") })
        {
            var context = PostgresFixture.CreateTenantContext(tenant);
            await using var ctx = db.CreateDbContext(context);
            var repo = new AuditLogRepository(ctx, context);
            repo.Add(tenant, null, action);
            await repo.SaveChangesAsync(CancellationToken.None);
        }

        var contextB = PostgresFixture.CreateTenantContext(tenantB);
        await using var asB = db.CreateDbContext(contextB);
        var logs = await new AuditLogRepository(asB, contextB).ListAsync(tenantB, 100, CancellationToken.None);

        logs.ShouldHaveSingleItem().Action.ShouldBe("b.action");
    }

    [Fact]
    public async Task Own_tenant_can_read_and_update_its_data()
    {
        var (tenantA, ownerA) = await db.CreateTenantAsync("A");
        var contextA = PostgresFixture.CreateTenantContext(tenantA);

        await using (var asA = db.CreateDbContext(contextA))
        {
            var repo = new TenantRepository(asA, contextA);
            var tenant = (await repo.GetAsync(tenantA, CancellationToken.None)).ShouldNotBeNull();
            tenant.BotName = "Huyệt Đạo";
            await repo.SaveChangesAsync(CancellationToken.None);
        }

        await using var check = db.CreateDbContext(tenantA);
        (await check.Tenants.SingleAsync()).BotName.ShouldBe("Huyệt Đạo");
        (await new MembershipRepository(check, contextA).ListAsync(tenantA, CancellationToken.None))
            .ShouldHaveSingleItem().UserId.ShouldBe(ownerA);
    }
}
