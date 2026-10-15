using System.Globalization;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Crm;

/// <summary>Đổi giữa UTC (lưu DB) và giờ địa phương của doanh nghiệp (<see cref="CrmSettings.TimeZone"/>).</summary>
public static class CrmClock
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public static TimeZoneInfo Zone(string? timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? CrmSettings.DefaultTimeZone : timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("VN", TimeSpan.FromHours(7), "Vietnam", "Vietnam");
        }
    }

    public static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone);

    public static DateTimeOffset FromLocal(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) => DateOnly.FromDateTime(ToLocal(utc, zone).DateTime);

    public static TimeOnly LocalTime(DateTimeOffset utc, TimeZoneInfo zone) => TimeOnly.FromDateTime(ToLocal(utc, zone).DateTime);

    /// <summary>"Thứ Bảy 18/10 15:00" cho tin nhắn khách.</summary>
    public static string FormatForCustomer(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = ToLocal(utc, zone);
        var day = local.DayOfWeek == DayOfWeek.Sunday ? "Chủ nhật" : $"Thứ {(int)local.DayOfWeek + 1}";
        return $"{day} {local:dd/MM} lúc {local:HH:mm}";
    }

    /// <summary>"Thứ Bảy, 18/10/2026 15:00" đầy đủ (prompt AI, nhật ký).</summary>
    public static string FormatFull(DateTimeOffset utc, TimeZoneInfo zone) =>
        ToLocal(utc, zone).ToString("dddd, dd/MM/yyyy HH:mm", Vietnamese);
}
