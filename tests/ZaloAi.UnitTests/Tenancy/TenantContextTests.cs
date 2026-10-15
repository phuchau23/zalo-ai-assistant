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

    [Fact]
    public void Owner_and_system_jobs_can_do_everything_and_see_all_branches()
    {
        var owner = new TenantContext();
        owner.Set(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner, false, permissions: [], branchId: Guid.NewGuid());
        owner.Can(TenantPermissions.VoidInvoice).ShouldBeTrue();
        owner.BranchId.ShouldBeNull();

        var job = new TenantContext();
        job.Set(Guid.NewGuid(), null, null, false);
        job.Can(TenantPermissions.ManageCatalog).ShouldBeTrue();
        job.BranchId.ShouldBeNull();
    }

    [Fact]
    public void Staff_only_has_granted_known_permissions_and_is_limited_to_branch()
    {
        var branch = Guid.NewGuid();
        var staff = new TenantContext();
        staff.Set(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Staff, false, [TenantPermissions.ManageCatalog, "made_up"], branch);

        staff.Can(TenantPermissions.ManageCatalog).ShouldBeTrue();
        staff.Can(TenantPermissions.ManageStaff).ShouldBeFalse();
        staff.Permissions.ShouldBe([TenantPermissions.ManageCatalog]);
        staff.BranchId.ShouldBe(branch);

        var roaming = new TenantContext();
        roaming.Set(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Staff, false, [TenantPermissions.ViewAllBranches], branch);
        roaming.BranchId.ShouldBeNull(); // quyền xem mọi chi nhánh bỏ giới hạn
    }

    [Fact]
    public void Permission_catalog_is_consistent()
    {
        TenantPermissions.All.ShouldBeUnique();
        TenantPermissions.All.ShouldAllBe(p => TenantPermissions.IsKnown(p));
        TenantPermissions.IsKnown("nope").ShouldBeFalse();
        TenantPermissions.IsKnown(null).ShouldBeFalse();
    }
}
