using Shouldly;
using ZaloAi.Infrastructure.Dashboard;

namespace ZaloAi.UnitTests.Dashboard;

public sealed class DashboardStatsTests
{
    [Fact]
    public void Window_uses_vietnam_days()
    {
        // 18:30 UTC ngày 13/10 = 01:30 sáng 14/10 giờ Việt Nam → hôm nay là 14/10.
        var window = DashboardStats.Window(new DateTimeOffset(2026, 10, 13, 18, 30, 0, TimeSpan.Zero), 7);

        window.LastDay.ShouldBe(new DateOnly(2026, 10, 14));
        window.FirstDay.ShouldBe(new DateOnly(2026, 10, 8));
        window.From.ShouldBe(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(7)));
        window.To.ShouldBe(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.FromHours(7)));
        window.MonthStart.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(7)));
    }

    [Fact]
    public void Daily_counts_distinct_conversations_per_vietnam_day_and_fills_gaps()
    {
        var window = DashboardStats.Window(new DateTimeOffset(2026, 10, 14, 5, 0, 0, TimeSpan.Zero), 3);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var vn = TimeSpan.FromHours(7);
        var activity = new[]
        {
            (a, new DateTimeOffset(2026, 10, 12, 23, 50, 0, vn)),
            (a, new DateTimeOffset(2026, 10, 12, 8, 0, 0, vn)),
            (b, new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero)), // = 00:00 ngày 13 giờ VN
            (a, new DateTimeOffset(2026, 10, 14, 9, 0, 0, vn)),
        };

        DashboardStats.Daily(window, activity).Select(d => (d.Day.Day, d.Conversations)).ShouldBe([(12, 1), (13, 1), (14, 1)]);
    }

    [Fact]
    public void Bot_handled_excludes_conversations_that_needed_a_human()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        DashboardStats.BotHandled([a, a, b], [b]).ShouldBe(1);
    }
}
