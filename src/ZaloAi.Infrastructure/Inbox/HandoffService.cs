using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Inbox;

/// <summary>Đọc/ghi cài đặt chuyển tiếp (1 dòng / tenant, chưa có → giá trị mặc định).</summary>
public sealed class HandoffSettingsRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Được track nếu đã có; chưa có → đối tượng mặc định đã Add (lưu khi SaveChanges).</summary>
    public async Task<HandoffSettings> GetOrCreateAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var settings = await Db.HandoffSettings.Where(h => h.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new HandoffSettings { TenantId = tenantId };
        Db.HandoffSettings.Add(settings);
        return settings;
    }

    /// <summary>Chỉ đọc: chưa có thì trả mặc định (không ghi DB).</summary>
    public async Task<HandoffSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.HandoffSettings.AsNoTracking().Where(h => h.TenantId == tenantId).FirstOrDefaultAsync(cancellationToken)
            ?? new HandoffSettings { TenantId = tenantId };
    }
}

/// <summary>Câu chuyển tiếp (docs/FEATURE-SPECS.md mục 1) và giờ làm việc theo giờ Việt Nam.</summary>
public static class HandoffTexts
{
    /// <summary>Cách gọi khách mặc định (khớp mẫu ngành).</summary>
    public const string CustomerAddress = "anh/chị";

    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");

    public static bool IsWorkingTime(HandoffSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var local = TimeZoneInfo.ConvertTime(now, Vietnam);
        if ((settings.WorkingDays & (1 << (int)local.DayOfWeek)) == 0)
        {
            return false;
        }

        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return TryParse(settings.OpenTime, out var open) && TryParse(settings.CloseTime, out var close)
            && time >= open && time < close;
    }

    /// <summary>"8:00 sáng thứ Hai" / "8:00 sáng mai" — lần mở cửa kế tiếp.</summary>
    public static string NextOpening(HandoffSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryParse(settings.OpenTime, out var open) || settings.WorkingDays == 0)
        {
            return "giờ làm việc tiếp theo";
        }

        var local = TimeZoneInfo.ConvertTime(now, Vietnam);
        for (var addDays = 0; addDays <= 7; addDays++)
        {
            var day = local.Date.AddDays(addDays);
            if ((settings.WorkingDays & (1 << (int)day.DayOfWeek)) == 0 || (addDays == 0 && TimeOnly.FromTimeSpan(local.TimeOfDay) >= open))
            {
                continue;
            }

            var hour = open.ToString("H:mm", CultureInfo.InvariantCulture);
            var when = addDays switch
            {
                0 => "hôm nay",
                1 => "ngày mai",
                _ => DayName(day.DayOfWeek),
            };
            return $"{hour} {when}";
        }

        return "giờ làm việc tiếp theo";
    }

    public static string Handoff(HandoffSettings settings, string businessName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var template = IsWorkingTime(settings, now) ? settings.HandoffMessage : settings.AfterHoursMessage;
        return Fill(template, businessName, staffName: null, settings, now);
    }

    public static string Fill(string template, string businessName, string? staffName, HandoffSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(settings);
        var text = template
            .Replace("{xung_ho}", CustomerAddress, StringComparison.Ordinal)
            .Replace("{ten_doanh_nghiep}", businessName, StringComparison.Ordinal)
            .Replace("{ten_nhan_vien}", string.IsNullOrWhiteSpace(staffName) ? "chuyên viên" : staffName, StringComparison.Ordinal)
            .Replace("{thoi_gian_phan_hoi}", settings.ResponseTime, StringComparison.Ordinal);
        if (text.Contains("{gio_mo_cua_tiep_theo}", StringComparison.Ordinal))
        {
            text = text.Replace("{gio_mo_cua_tiep_theo}", NextOpening(settings, now), StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Chữ ký cuối tin nhân viên.</summary>
    public static string Sign(string text, string staffName) => $"{text.TrimEnd()}\n— {staffName}, CSKH";

    public static bool TryParse(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "thứ Hai",
        DayOfWeek.Tuesday => "thứ Ba",
        DayOfWeek.Wednesday => "thứ Tư",
        DayOfWeek.Thursday => "thứ Năm",
        DayOfWeek.Friday => "thứ Sáu",
        DayOfWeek.Saturday => "thứ Bảy",
        _ => "Chủ nhật",
    };
}
