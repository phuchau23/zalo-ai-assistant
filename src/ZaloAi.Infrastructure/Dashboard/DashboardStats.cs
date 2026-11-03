namespace ZaloAi.Infrastructure.Dashboard;

/// <summary>Khoảng thời gian dashboard theo ngày giờ Việt Nam: [From, To) gồm <c>days</c> ngày, ngày cuối là hôm nay.</summary>
public sealed record DashboardWindow(DateOnly FirstDay, DateOnly LastDay, DateTimeOffset From, DateTimeOffset To, DateTimeOffset MonthStart);

public sealed record DailyCount(DateOnly Day, int Conversations);

/// <summary>Tính số liệu dashboard (thuần, không đọc database) — gộp theo ngày giờ Việt Nam.</summary>
public static class DashboardStats
{
    public static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public static DashboardWindow Window(DateTimeOffset now, int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var today = DateOnly.FromDateTime(now.ToOffset(Vietnam).DateTime);
        var first = today.AddDays(-(days - 1));
        return new DashboardWindow(
            first,
            today,
            StartOf(first),
            StartOf(today.AddDays(1)),
            StartOf(new DateOnly(today.Year, today.Month, 1)));
    }

    /// <summary>Số hội thoại (khách khác nhau có nhắn) mỗi ngày, đủ mọi ngày trong khoảng (ngày không có = 0).</summary>
    public static IReadOnlyList<DailyCount> Daily(DashboardWindow window, IEnumerable<(Guid ConversationId, DateTimeOffset At)> activity)
    {
        ArgumentNullException.ThrowIfNull(window);
        var perDay = activity
            .GroupBy(a => DateOnly.FromDateTime(a.At.ToOffset(Vietnam).DateTime))
            .ToDictionary(g => g.Key, g => g.Select(a => a.ConversationId).Distinct().Count());
        var result = new List<DailyCount>();
        for (var day = window.FirstDay; day <= window.LastDay; day = day.AddDays(1))
        {
            result.Add(new DailyCount(day, perDay.GetValueOrDefault(day)));
        }

        return result;
    }

    /// <summary>Hội thoại trong khoảng mà bot tự xử lý hết (không chuyển nhân viên, nhân viên không phải nhắn).</summary>
    public static int BotHandled(IEnumerable<Guid> conversations, IEnumerable<Guid> neededHuman) =>
        conversations.Distinct().Except(neededHuman).Count();

    /// <summary>00:00 giờ Việt Nam của ngày đó, quy về UTC (Npgsql chỉ ghi được timestamptz offset 0).</summary>
    private static DateTimeOffset StartOf(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Vietnam).ToUniversalTime();
}
