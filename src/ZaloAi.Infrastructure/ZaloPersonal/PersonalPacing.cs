using System.Globalization;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Giới hạn đang áp cho một nick hôm nay (docs/ZALO-PERSONAL-PLAN.md mục 7, 7.1, 7.2).</summary>
/// <param name="NewNickWeek">1 hoặc 2 nếu nick mới kết nối (đang ở bậc giới hạn thấp), null nếu đã qua.</param>
public sealed record PacingLimits(
    int InvitesPerDay,
    int NewContactsPerDay,
    int MessagesPerDay,
    int MessagesPerHour,
    int FindUserPerDay,
    int? NewNickWeek);

/// <summary>Số đã dùng hôm nay (giờ Việt Nam).</summary>
public sealed record PacingUsage(int Invites, int NewContacts, int Messages, int FindUser);

/// <summary>
/// Nhịp gửi của nick Zalo cá nhân. Hệ thống KHÔNG dò ngưỡng khóa của Zalo và không bao giờ tự tăng quá mức cấu hình: chỉ giảm khi nick
/// còn mới hoặc khách phản hồi xấu (phanh). DN chọn "An toàn" (hệ thống tự đặt số) hoặc "Tùy chỉnh" (tự nhập, kẹp trong trần).
/// </summary>
public static class PersonalPacing
{
    /// <summary>Mặc định chế độ an toàn khi nick đã qua bậc nick mới.</summary>
    public const int SafeInvitesPerDay = 10;

    public const int SafeNewContactsPerDay = 20;

    public const int SafeMessagesPerDay = 200;

    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    public static PacingLimits Effective(ZaloPersonalAccount account, ZaloPersonalOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(options);

        // Nick mới kết nối: tuần đầu 3 lời mời/ngày, tuần hai 5 (chỉ lời mời — kiểu hành vi dễ bị hạn chế nhất).
        var age = now - account.CreatedAt;
        int? week = age < TimeSpan.FromDays(7) ? 1 : age < TimeSpan.FromDays(14) ? 2 : null;
        var rampInvites = week switch { 1 => 3, 2 => 5, _ => int.MaxValue };

        int invites, newContacts, messages;
        if (account.PacingMode == PacingMode.Custom)
        {
            invites = account.CustomInvitesPerDay ?? SafeInvitesPerDay;
            newContacts = account.CustomNewContactsPerDay ?? SafeNewContactsPerDay;
            messages = account.CustomMessagesPerDay ?? SafeMessagesPerDay;
        }
        else
        {
            invites = SafeInvitesPerDay;
            newContacts = SafeNewContactsPerDay;
            messages = SafeMessagesPerDay;
        }

        invites = Math.Min(invites, rampInvites);

        // Phanh: luôn bật ở chế độ an toàn; chế độ tùy chỉnh theo lựa chọn của DN.
        var brake = account.PacingMode == PacingMode.Safe || account.AutoBrake;
        if (brake && account.OutreachLevel == OutreachLevel.Slow)
        {
            invites /= 2;
            newContacts /= 2;
        }
        else if (brake && account.OutreachLevel == OutreachLevel.Paused)
        {
            invites = 0;
            newContacts = 0;
        }

        return new PacingLimits(
            Math.Clamp(invites, 0, options.MaxInvitesPerDay),
            Math.Clamp(newContacts, 0, options.MaxNewContactsPerDay),
            Math.Clamp(messages, 1, options.MaxMessagesPerDay),
            options.MessagesPerHour,
            options.FindUserPerDay,
            week);
    }

    /// <summary>Đang là ban đêm theo giờ Việt Nam (khung có thể qua nửa đêm, vd 22:00–07:00). Cấu hình sai → coi như không có đêm.</summary>
    public static bool IsNight(ZaloPersonalOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TimeOnly.TryParseExact(options.NightStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(options.NightEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || start == end)
        {
            return false;
        }

        var local = TimeOnly.FromTimeSpan(now.ToOffset(Vietnam).TimeOfDay);
        return start < end ? local >= start && local < end : local >= start || local < end;
    }

    /// <summary>
    /// Số tin tối đa mỗi lượt bot trả lời qua nick (chủ dự án chốt 2026-11-15 — Graceful Degradation): bình thường để tự nhiên 1–4 tin;
    /// nick đã dùng ≥ 70% lượt gửi trong ngày hoặc trong giờ → tối đa 2; ≥ 90% → 1; ban đêm → 1. Mỗi tin trừ 1 lượt của nick.
    /// </summary>
    public static int ReplyParts(double load, bool night) => night || load >= 0.9 ? 1 : load >= 0.7 ? 2 : NaturalParts;

    /// <summary>Số tin "tự nhiên" tối đa của giọng "Như nhân viên thật".</summary>
    public const int NaturalParts = 4;

    /// <summary>Ngày theo giờ Việt Nam (khóa bộ đếm theo ngày).</summary>
    public static string DayKey(DateTimeOffset now) => now.ToOffset(Vietnam).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string HourKey(DateTimeOffset now) => now.ToOffset(Vietnam).ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
}

/// <summary>Loại lượt đếm của nick trong ngày.</summary>
public enum PacingCounter
{
    Messages,
    Invites,
    NewContacts,
    FindUser,
}

/// <summary>Bộ đếm theo cửa sổ ngày/giờ trên Redis (cùng kỹ thuật chặn tốc độ gửi OA).</summary>
public sealed class PersonalSendLimiter(IDistributedStore store, TimeProvider time)
{
    /// <summary>Tăng bộ đếm ngày (và giờ với tin nhắn); trả số SAU khi tăng.</summary>
    public async Task<(long Day, long Hour)> CountAsync(Guid connectionId, PacingCounter counter, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var day = await store.IncrementAsync(DayKey(connectionId, counter, now), TimeSpan.FromHours(26), cancellationToken);
        var hour = counter == PacingCounter.Messages
            ? await store.IncrementAsync(HourKey(connectionId, now), TimeSpan.FromMinutes(70), cancellationToken)
            : 0;
        return (day, hour);
    }

    /// <summary>Còn lượt không (không tăng). Dùng trước khi xếp tin chủ động / lời mời.</summary>
    public async Task<bool> HasRoomAsync(Guid connectionId, PacingCounter counter, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return false;
        }

        var raw = await store.GetAsync(DayKey(connectionId, counter, time.GetUtcNow()), cancellationToken);
        return !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var used) || used < limit;
    }

    /// <summary>Số tin đã gửi qua nick trong ngày và trong giờ hiện tại (không tăng).</summary>
    public async Task<(int Day, int Hour)> MessageLoadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        static int Parse(string? raw) => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (Parse(await store.GetAsync(DayKey(connectionId, PacingCounter.Messages, now), cancellationToken)),
            Parse(await store.GetAsync(HourKey(connectionId, now), cancellationToken)));
    }

    public async Task<PacingUsage> UsageAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        async Task<int> Read(PacingCounter c) =>
            int.TryParse(await store.GetAsync(DayKey(connectionId, c, now), cancellationToken), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new PacingUsage(
            await Read(PacingCounter.Invites), await Read(PacingCounter.NewContacts), await Read(PacingCounter.Messages), await Read(PacingCounter.FindUser));
    }

    /// <summary>Số tin tối đa cho lượt trả lời sắp gửi qua nick này (<see cref="PersonalPacing.ReplyParts"/>).</summary>
    public async Task<int> ReplyPartsAsync(ZaloPersonalAccount account, ZaloPersonalOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(options);
        var now = time.GetUtcNow();
        var limits = PersonalPacing.Effective(account, options, now);
        var (day, hour) = await MessageLoadAsync(account.ConnectionId, cancellationToken);
        var load = Math.Max(day / (double)Math.Max(1, limits.MessagesPerDay), hour / (double)Math.Max(1, limits.MessagesPerHour));
        return PersonalPacing.ReplyParts(load, PersonalPacing.IsNight(options, now));
    }

    private static string DayKey(Guid connectionId, PacingCounter counter, DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"zp:day:{connectionId:N}:{PersonalPacing.DayKey(now)}:{counter.ToString().ToLowerInvariant()}");

    private static string HourKey(Guid connectionId, DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"zp:hour:{connectionId:N}:{PersonalPacing.HourKey(now)}");
}
