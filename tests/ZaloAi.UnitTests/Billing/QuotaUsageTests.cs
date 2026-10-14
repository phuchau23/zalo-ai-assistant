using Shouldly;
using ZaloAi.Infrastructure.Billing;

namespace ZaloAi.UnitTests.Billing;

public sealed class QuotaUsageTests
{
    [Theory]
    [InlineData(100, 80)]
    [InlineData(5, 4)]
    [InlineData(1, 1)]
    public void Warning_starts_at_80_percent_rounded_up(int quota, int warningAt) => QuotaUsage.WarningAt(quota).ShouldBe(warningAt);

    [Fact]
    public void Flags_follow_usage()
    {
        new QuotaUsage(79, 100).ShouldSatisfyAllConditions(u => u.Warning.ShouldBeFalse(), u => u.Exceeded.ShouldBeFalse());
        new QuotaUsage(80, 100).ShouldSatisfyAllConditions(u => u.Warning.ShouldBeTrue(), u => u.Exceeded.ShouldBeFalse());
        new QuotaUsage(100, 100).Exceeded.ShouldBeTrue();
        new QuotaUsage(5000, null).ShouldSatisfyAllConditions(u => u.Warning.ShouldBeFalse(), u => u.Exceeded.ShouldBeFalse());
    }
}
