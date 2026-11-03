using Shouldly;
using ZaloAi.Ai.Reports;
using ZaloAi.Infrastructure.Reports;

namespace ZaloAi.UnitTests.Reports;

public sealed class ReportQuestionInterpreterTests
{
    [Fact]
    public void Parses_valid_json_and_falls_back_on_garbage()
    {
        var q = ReportQuestionInterpreter.Parse("""{"report":"customers","from":"2026-10-01","to":"2026-10-31","group_by":null,"focus":"khách quay lại","unanswerable":false,"explanation":null}""");
        (q.Report, q.From, q.To, q.Focus, q.Unanswerable).ShouldBe(("customers", "2026-10-01", "2026-10-31", "khách quay lại", false));

        ReportQuestionInterpreter.Parse("""{"report":"weird","unanswerable":false}""").Report.ShouldBe("revenue");
        var bad = ReportQuestionInterpreter.Parse("not json");
        bad.Unanswerable.ShouldBeTrue();
    }

    [Fact]
    public void Period_keys_group_by_day_week_month()
    {
        var wednesday = new DateOnly(2026, 10, 14);
        ReportService.PeriodKey(wednesday, "day").ShouldBe("2026-10-14");
        ReportService.PeriodKey(wednesday, "week").ShouldBe("2026-10-12"); // thứ 2
        ReportService.PeriodKey(wednesday, "month").ShouldBe("2026-10");
    }
}
