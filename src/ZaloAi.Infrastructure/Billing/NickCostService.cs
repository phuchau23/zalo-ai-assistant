using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>Chi phí một ngày của một kênh/nick: USD gốc, giá bán USD ngày đó, VND (đã gồm phí thẻ).</summary>
/// <param name="RateApproximated">Không lấy được tỷ giá đúng ngày → dùng giá ngày gần nhất.</param>
public sealed record NickCostDay(DateOnly Date, decimal CostUsd, decimal UsdVnd, decimal CostVnd, bool RateApproximated);

/// <summary>Chi phí AI của một kênh/nick trong kỳ, kèm kết quả để tính đ/người.</summary>
/// <param name="ConnectionId">null = phần không thuộc nick nào (Chat thử, kho kiến thức, hỏi số liệu...).</param>
/// <param name="Channel">zalopersonal | zalo | null.</param>
public sealed record NickCost(
    Guid? ConnectionId,
    string? Name,
    string? Channel,
    decimal CostUsd,
    decimal CostVnd,
    int ChatCalls,
    int EmbedCalls,
    long InputTokens,
    long OutputTokens,
    int ContactsReached,
    int ContactsReplied,
    int Appointments,
    IReadOnlyList<NickCostDay> Days);

/// <summary>Báo cáo chi phí AI theo nick của một doanh nghiệp, kỳ [From, To] theo ngày giờ Việt Nam.</summary>
public sealed record NickCostReport(
    DateOnly From,
    DateOnly To,
    string RateSource,
    decimal CardFeePercent,
    decimal? AverageRate,
    IReadOnlyList<DateOnly> ApproximatedDays,
    IReadOnlyList<NickCost> Nicks,
    NickCost Other,
    decimal TotalUsd,
    decimal TotalVnd);

/// <summary>
/// Tính chi phí AI theo từng kênh/nick ra tiền Việt (chủ dự án chốt 2026-11-15). Cách tính để khớp tiền thật nhất có thể:
/// - USD gốc của từng lần gọi = token nhà cung cấp báo × bảng giá chính thức (đã trừ phần cache), lưu 8 số lẻ lúc gọi.
/// - Cộng USD theo NGÀY (giờ VN) rồi nhân giá BÁN USD của Vietcombank đúng ngày đó, cộng phí thẻ (<see cref="CostOptions.CardFeePercent"/>).
///   Không làm tròn ở giữa; giao diện mới làm tròn tới đồng.
/// - Kết quả công việc (người đã nhắn/trò chuyện, khách trả lời, lịch hẹn tạo trong kỳ) để tính đ/người.
/// </summary>
public sealed class NickCostService(
    UsageCostRepository usage,
    ChannelConnectionRepository connections,
    ExchangeRateService rates,
    IOptions<CostOptions> options)
{
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    /// <param name="from">Ngày đầu (giờ VN); null = ngày 1 tháng này.</param>
    /// <param name="to">Ngày cuối, tính cả ngày (giờ VN); null = hôm nay.</param>
    public async Task<NickCostReport> ReportAsync(Guid tenantId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var today = rates.VietnamToday();
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? today;
        if (end < start)
        {
            throw new InvalidInputException("Ngày cuối phải sau ngày đầu.");
        }

        if (end.DayNumber - start.DayNumber + 1 > options.Value.MaxReportDays)
        {
            throw new InvalidInputException($"Mỗi lần xem tối đa {options.Value.MaxReportDays} ngày.");
        }

        var fromUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), Vietnam).ToUniversalTime(); // Npgsql chỉ nhận UTC
        var toUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), Vietnam).ToUniversalTime();
        var rows = await usage.RowsAsync(tenantId, fromUtc, toUtc, cancellationToken);
        var activity = (await usage.ActivityAsync(tenantId, fromUtc, toUtc, cancellationToken)).ToDictionary(a => a.ConnectionId);
        var table = await rates.UsdRatesAsync(start, end, cancellationToken);
        var approximated = table.Approximated.ToHashSet();
        var feeFactor = 1m + (options.Value.CardFeePercent / 100m);
        var channels = (await connections.ListAsync(tenantId, cancellationToken)).ToDictionary(c => c.Id);

        NickCost Build(Guid? id, IEnumerable<UsageCostRow> list)
        {
            var items = list.ToList();
            var days = items
                .GroupBy(r => DateOnly.FromDateTime(r.CreatedAt.ToOffset(Vietnam).DateTime))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var usd = g.Sum(r => r.CostUsd);
                    var rate = table.Sell.TryGetValue(g.Key, out var r) ? r : 0m;
                    return new NickCostDay(g.Key, usd, rate, usd * rate * feeFactor, approximated.Contains(g.Key));
                })
                .ToList();
            var connection = id is { } cid ? channels.GetValueOrDefault(cid) : null;
            var done = id is { } aid ? activity.GetValueOrDefault(aid) : null;
            return new NickCost(
                id,
                connection?.Name,
                connection is null ? null : connection.Channel.ToString().ToLowerInvariant(),
                days.Sum(d => d.CostUsd),
                days.Sum(d => d.CostVnd),
                items.Count(r => r.Kind == UsageKind.Chat),
                items.Count(r => r.Kind == UsageKind.Embed),
                items.Sum(r => (long)r.InputTokens),
                items.Sum(r => (long)r.OutputTokens),
                done?.ContactsReached ?? 0,
                done?.ContactsReplied ?? 0,
                done?.Appointments ?? 0,
                days);
        }

        // Mọi nick Zalo cá nhân đều hiện (kể cả chưa tốn đồng nào); kênh khác chỉ hiện khi có chi phí.
        var ids = rows.Where(r => r.ConnectionId is not null).Select(r => r.ConnectionId!.Value)
            .Concat(channels.Values.Where(c => c.Channel == ChannelKind.ZaloPersonal).Select(c => c.Id))
            .Distinct()
            .ToList();
        var byConnection = rows.Where(r => r.ConnectionId is not null).ToLookup(r => r.ConnectionId!.Value);
        var nicks = ids.Select(id => Build(id, byConnection[id])).OrderByDescending(n => n.CostVnd).ToList();
        var other = Build(null, rows.Where(r => r.ConnectionId is null));
        var usedRates = table.Sell.Where(kv => rows.Any(r => DateOnly.FromDateTime(r.CreatedAt.ToOffset(Vietnam).DateTime) == kv.Key)).Select(kv => kv.Value).ToList();
        return new NickCostReport(
            start,
            end,
            VietcombankRateClient.Source,
            options.Value.CardFeePercent,
            usedRates.Count > 0 ? usedRates.Average() : table.Sell.Values.Cast<decimal?>().LastOrDefault(),
            table.Approximated.Where(d => rows.Any(r => DateOnly.FromDateTime(r.CreatedAt.ToOffset(Vietnam).DateTime) == d)).ToList(),
            nicks,
            other,
            nicks.Sum(n => n.CostUsd) + other.CostUsd,
            nicks.Sum(n => n.CostVnd) + other.CostVnd);
    }
}
