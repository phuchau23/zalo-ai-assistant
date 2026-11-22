using System.Globalization;
using System.Text.Json;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>Tỷ giá một ngày của một ngoại tệ (VND cho 1 đơn vị).</summary>
public sealed record DailyRate(DateOnly Date, decimal Sell, decimal Transfer);

/// <summary>
/// Tỷ giá công khai của Vietcombank (kiểm chứng 2026-11-15, có cả ngày cũ, cả thứ 7/chủ nhật):
/// GET https://www.vietcombank.com.vn/api/exchangerates?date=2026-11-14 →
/// { "Date": "2026-11-14T00:00:00", "UpdatedDate": "2026-11-14T23:00:00+07:00", "Data": [ { "currencyCode": "USD", "cash": "25500.00",
/// "transfer": "25530.00", "sell": "25910.00" }, ... ] }. "0.00" = không có giá. Trang ghi rõ "chỉ mang tính chất tham khảo".
/// Typed HttpClient + resilience (timeout, retry). Không có dữ liệu cá nhân trong request.
/// </summary>
public sealed class VietcombankRateClient(HttpClient http)
{
    public const string Source = "vietcombank";

    /// <returns>null nếu Vietcombank không có giá ngoại tệ này cho ngày đó.</returns>
    public async Task<DailyRate?> GetAsync(string currency, DateOnly date, CancellationToken cancellationToken)
    {
        var url = $"api/exchangerates?date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Parse(json.RootElement, currency, date);
    }

    /// <summary>Đọc một ngoại tệ từ phản hồi (tách riêng để test không cần mạng).</summary>
    public static DailyRate? Parse(JsonElement root, string currency, DateOnly date)
    {
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("currencyCode", out var code) || !string.Equals(code.GetString()?.Trim(), currency, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sell = Number(item, "sell");
            return sell is > 0 ? new DailyRate(date, sell.Value, Number(item, "transfer") ?? 0m) : null;
        }

        return null;
    }

    private static decimal? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && decimal.TryParse(value.GetString()?.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}
