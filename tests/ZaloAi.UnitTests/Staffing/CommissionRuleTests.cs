using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Staffing;

namespace ZaloAi.UnitTests.Staffing;

public sealed class CommissionRuleTests
{
    private static readonly Guid Lan = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();

    private static CommissionRule Rule(Guid? staff, CommissionScope scope, long value, Guid? item = null, bool active = true) =>
        new() { StaffMemberId = staff, Scope = scope, CatalogItemId = item, Kind = CommissionKind.Percent, Value = value, IsActive = active };

    [Fact]
    public void Most_specific_rule_wins()
    {
        var rules = new[]
        {
            Rule(null, CommissionScope.All, 5),
            Rule(Lan, CommissionScope.All, 6),
            Rule(null, CommissionScope.Service, 10),
            Rule(Lan, CommissionScope.Service, 12),
            Rule(null, CommissionScope.Item, 20, Item),
            Rule(Lan, CommissionScope.Item, 25, Item),
        };

        CommissionService.Resolve(rules, Lan, CatalogKind.Service, Item)!.Value.ShouldBe(25);
        CommissionService.Resolve(rules, Guid.NewGuid(), CatalogKind.Service, Item)!.Value.ShouldBe(20);
        CommissionService.Resolve(rules, Lan, CatalogKind.Service, Guid.NewGuid())!.Value.ShouldBe(12);
        CommissionService.Resolve(rules, Guid.NewGuid(), CatalogKind.Service, null)!.Value.ShouldBe(10);
        CommissionService.Resolve(rules, Lan, CatalogKind.Product, null)!.Value.ShouldBe(6);
        CommissionService.Resolve(rules, Guid.NewGuid(), CatalogKind.Product, null)!.Value.ShouldBe(5);
    }

    [Fact]
    public void Inactive_rules_are_ignored_and_none_means_no_commission()
    {
        CommissionService.Resolve([Rule(null, CommissionScope.All, 5, active: false)], Lan, CatalogKind.Service, null).ShouldBeNull();
        CommissionService.Resolve([], Lan, CatalogKind.Service, null).ShouldBeNull();
    }
}
