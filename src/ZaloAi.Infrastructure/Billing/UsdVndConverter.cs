using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>
/// Quy chi phí USD ra VND theo giá bán USD Vietcombank của NGÀY phát sinh (giờ Việt Nam) × (1 + phí thẻ) — dùng chung cho mọi màn hình
/// hiện chi phí AI bằng tiền Việt (chủ dự án chốt 2026-11-17: bỏ tỷ giá cố định 26.000 đ/USD). Ngày không lấy được tỷ giá → giá ngày
/// gần nhất (<see cref="ExchangeRateService"/>); chưa có tỷ giá nào → null (giao diện hiện USD).
/// </summary>
public sealed class UsdVndConverter(ExchangeRateService rates, IOptions<CostOptions> options)
{
    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public static DateOnly VietnamDate(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Vietnam).DateTime);

    /// <summary>Hàm quy đổi cho mọi thời điểm trong [<paramref name="since"/>, <paramref name="until"/>].</summary>
    public async Task<Func<DateTimeOffset, decimal, decimal?>> ForRangeAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var table = await rates.UsdRatesAsync(VietnamDate(since), VietnamDate(until), cancellationToken);
        var fee = 1m + (options.Value.CardFeePercent / 100m);
        return (at, usd) => table.Sell.TryGetValue(VietnamDate(at), out var rate) ? usd * rate * fee : null;
    }

    /// <summary>Tổng VND của các khoản (thời điểm, USD); null nếu có khoản không quy đổi được.</summary>
    public async Task<decimal?> SumAsync(IReadOnlyCollection<(DateTimeOffset At, decimal Usd)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0m;
        }

        var toVnd = await ForRangeAsync(rows.Min(r => r.At), rows.Max(r => r.At), cancellationToken);
        decimal total = 0;
        foreach (var (at, usd) in rows)
        {
            if (toVnd(at, usd) is not { } vnd)
            {
                return null;
            }

            total += vnd;
        }

        return total;
    }
}
