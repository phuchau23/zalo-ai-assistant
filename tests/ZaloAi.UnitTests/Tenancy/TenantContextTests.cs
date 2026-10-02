using Shouldly;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.UnitTests.Tenancy;

public sealed class TenantContextTests
{
    [Fact]
    public void RequireTenantId_throws_when_not_set()
    {
        Should.Throw<InvalidOperationException>(() => new TenantContext().RequireTenantId());
    }

    [Fact]
    public void Set_then_read_values()
    {
        var tenantId = Guid.NewGuid();
        var context = new TenantContext();

        context.Set(tenantId, Guid.NewGuid(), TenantRole.Owner, isSuperAdmin: false);

        context.RequireTenantId().ShouldBe(tenantId);
        context.Role.ShouldBe(TenantRole.Owner);
    }

    [Fact]
    public void Set_twice_in_same_scope_throws()
    {
        var context = new TenantContext();
        context.Set(Guid.NewGuid(), null, TenantRole.Staff, isSuperAdmin: false);

        Should.Throw<InvalidOperationException>(() => context.Set(Guid.NewGuid(), null, TenantRole.Staff, isSuperAdmin: false));
    }
}
