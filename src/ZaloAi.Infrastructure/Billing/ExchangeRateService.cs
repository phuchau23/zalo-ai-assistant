using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>Giá bán USD theo ngày; <see cref="Approximated"/> = ngày không lấy được giá, dùng giá ngày gần nhất.</summary>
public sealed record UsdRateTable(IReadOnlyDictionary<DateOnly, decimal> Sell, IReadOnlyList<DateOnly> Approximated);

/// <summary>
/// Tỷ giá USD→VND theo ngày (bảng hệ thống exchange_rates, không thuộc tenant — dùng DbContext trực tiếp là được). Ngày còn thiếu thì
/// hỏi Vietcombank rồi lưu lại; Vietcombank lỗi thì dùng giá ngày gần nhất và đánh dấu "ước tính" để giao diện ghi rõ.
/// </summary>
public sealed partial class ExchangeRateService(AppDbContext db, VietcombankRateClient vietcombank, TimeProvider time, ILogger<ExchangeRateService> logger)
{
    public const string Usd = "USD";

    /// <summary>Mỗi lần báo cáo hỏi Vietcombank tối đa chừng này ngày còn thiếu (phần còn lại để job đêm bù).</summary>
    private const int MaxFetchPerCall = 40;

    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public DateOnly VietnamToday() => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(Vietnam).DateTime);

    /// <summary>Giá bán USD cho mọi ngày trong [from, to] (giờ Việt Nam).</summary>
    public async Task<UsdRateTable> UsdRatesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var today = VietnamToday();
        var last = to > today ? today : to;
        var stored = await db.ExchangeRates.AsNoTracking()
            .Where(r => r.Currency == Usd && r.Date >= from.AddDays(-14) && r.Date <= last)
            .ToDictionaryAsync(r => r.Date, r => r.Sell, cancellationToken);

        var fetched = 0;
        for (var day = from; day <= last && fetched < MaxFetchPerCall; day = day.AddDays(1))
        {
            if (stored.ContainsKey(day))
            {
                continue;
            }

            fetched++;
            var rate = await TryFetchAsync(day, cancellationToken);
            if (rate is null)
            {
                break; // Vietcombank lỗi: đừng gọi dồn, phần còn lại dùng giá gần nhất
            }

            stored[day] = rate.Sell;
        }

        var sell = new Dictionary<DateOnly, decimal>();
        var approximated = new List<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (stored.TryGetValue(day, out var exact))
            {
                sell[day] = exact;
                continue;
            }

            // Ngày tương lai / không lấy được: giá gần nhất trước đó, không có thì gần nhất sau đó.
            var current = day;
            var nearest = stored.Where(kv => kv.Key <= current).OrderByDescending(kv => kv.Key).Select(kv => (decimal?)kv.Value).FirstOrDefault()
                          ?? stored.Where(kv => kv.Key > current).OrderBy(kv => kv.Key).Select(kv => (decimal?)kv.Value).FirstOrDefault();
            if (nearest is { } value)
            {
                sell[day] = value;
                approximated.Add(day);
            }
        }

        return new UsdRateTable(sell, approximated);
    }

    /// <summary>Job đêm: lấy lại (ghi đè) giá hôm nay + hôm qua, bù các ngày còn thiếu trong <paramref name="backfillDays"/> ngày qua.</summary>
    public async Task SyncAsync(int backfillDays, CancellationToken cancellationToken)
    {
        var today = VietnamToday();
        var since = today.AddDays(-backfillDays);
        var have = (await db.ExchangeRates.AsNoTracking()
                .Where(r => r.Currency == Usd && r.Date >= since)
                .Select(r => r.Date)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        for (var day = today; day >= since; day = day.AddDays(-1))
        {
            var refresh = day >= today.AddDays(-1); // Vietcombank chốt giá ngày lúc 23:00 → lấy lại hôm nay, hôm qua
            if ((refresh || !have.Contains(day)) && await TryFetchAsync(day, cancellationToken) is null)
            {
                return;
            }
        }
    }

    private async Task<DailyRate?> TryFetchAsync(DateOnly day, CancellationToken cancellationToken)
    {
        DailyRate? rate;
        try
        {
            rate = await vietcombank.GetAsync(Usd, day, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Mạng lỗi, quá giờ, Vietcombank đổi định dạng... → báo cáo vẫn chạy với giá ngày gần nhất (có đánh dấu).
            LogFetchFailed(logger, day, ex.GetType().Name);
            return null;
        }

        if (rate is null)
        {
            LogNoRate(logger, day);
            return null;
        }

        // Upsert: hai yêu cầu cùng lúc không làm hỏng khóa chính. Bảng hệ thống, không có tenant_id.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO exchange_rates (currency, date, sell, transfer, source, fetched_at)
             VALUES ({Usd}, {day}, {rate.Sell}, {rate.Transfer}, {VietcombankRateClient.Source}, {time.GetUtcNow()})
             ON CONFLICT (currency, date) DO UPDATE
             SET sell = EXCLUDED.sell, transfer = EXCLUDED.transfer, source = EXCLUDED.source, fetched_at = EXCLUDED.fetched_at
             """,
            cancellationToken);
        return rate;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không lấy được tỷ giá Vietcombank ngày {Day} ({Error}); dùng giá ngày gần nhất")]
    private static partial void LogFetchFailed(ILogger logger, DateOnly day, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vietcombank không có giá bán USD ngày {Day}")]
    private static partial void LogNoRate(ILogger logger, DateOnly day);
}
