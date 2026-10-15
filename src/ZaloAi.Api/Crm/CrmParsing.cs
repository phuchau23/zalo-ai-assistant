using System.Globalization;

namespace ZaloAi.Api.Crm;

/// <summary>Chuỗi "HH:mm" / "yyyy-MM-dd" trong request CRM ↔ TimeOnly / DateOnly (FE gửi chuỗi để không phụ thuộc định dạng JSON của .NET).</summary>
internal static class CrmParsing
{
    public static bool IsTime(string? value) =>
        value is not null && TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static bool IsDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static TimeOnly Time(string value) => TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);

    public static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Format(TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Format(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
