using System.Globalization;
using System.Text;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Crm;

/// <summary>
/// Câu cố định gửi khách về lịch hẹn (CRM C2) — do HỆ THỐNG soạn sau khi kiểm tra lịch, không để AI tự viết "đã đặt".
/// {bot} = cách bot xưng, {khach} = cách gọi khách (anh/chị/... hoặc "mình").
/// </summary>
public static class AppointmentTexts
{
    public static string Confirmed(string bot, string? salutation, DateTimeOffset startAt, TimeZoneInfo zone, string services, string? staffName, string branchName, string? address, bool multiBranch)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"Dạ {bot} đã giữ lịch cho {khach}: {CrmClock.FormatForCustomer(startAt, zone)}, {services}");
        if (staffName is not null)
        {
            s.Append(CultureInfo.InvariantCulture, $", kỹ thuật viên {staffName}");
        }

        if (multiBranch)
        {
            s.Append(CultureInfo.InvariantCulture, $", tại {branchName}");
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            s.Append(CultureInfo.InvariantCulture, $" ({address})");
        }

        s.Append(" ạ 🌿");
        return s.ToString();
    }

    public static string ConfirmedFollowUp(string bot, string? salutation, int cancelLeadHours)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"{Cap(khach)} đến sớm 5–10 phút giúp {bot} nha. Cần dời hay hủy, {khach} nhắn {bot} trước {cancelLeadHours} giờ là được ạ.";
    }

    public static string Alternatives(string bot, string? salutation, DateTimeOffset wanted, TimeZoneInfo zone, IReadOnlyList<DateTimeOffset> options)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        if (options.Count == 0)
        {
            return $"Dạ {CrmClock.FormatForCustomer(wanted, zone)} bên {bot} kín lịch rồi ạ. {Cap(khach)} cho {bot} xin một ngày hoặc khung giờ khác, {bot} kiểm tra ngay cho {khach} nha.";
        }

        var sameDay = options.All(o => CrmClock.LocalDate(o, zone) == CrmClock.LocalDate(wanted, zone));
        var list = string.Join(", ", options.Select(o => sameDay ? CrmClock.ToLocal(o, zone).ToString("HH:mm", CultureInfo.InvariantCulture) : CrmClock.FormatForCustomer(o, zone)));
        return $"Dạ {CrmClock.FormatForCustomer(wanted, zone)} bên {bot} kín rồi ạ. {Cap(khach)} chọn giúp {bot} {(sameDay ? "khung giờ" : "lịch")}: {list} — {khach} chọn giờ nào {bot} giữ chỗ liền ạ.";
    }

    public static string SlotList(string bot, string? salutation, DateOnly date, TimeZoneInfo zone, IReadOnlyList<DateTimeOffset> options)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        if (options.Count == 0)
        {
            return $"Dạ ngày {date:dd/MM} bên {bot} kín lịch rồi ạ. {Cap(khach)} xem ngày khác giúp {bot} nha.";
        }

        var list = string.Join(", ", options.Select(o => CrmClock.ToLocal(o, zone).ToString("HH:mm", CultureInfo.InvariantCulture)));
        return $"Dạ ngày {date:dd/MM} bên {bot} còn trống: {list}. {Cap(khach)} chọn giờ nào {bot} giữ chỗ cho {khach} ạ.";
    }

    public static string Cancelled(string bot, string? salutation, DateTimeOffset startAt, TimeZoneInfo zone)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"Dạ {bot} đã hủy lịch {CrmClock.FormatForCustomer(startAt, zone)} cho {khach} rồi ạ. Khi nào tiện {khach} nhắn {bot} đặt lại nha 💙";
    }

    public static string Rescheduled(string bot, string? salutation, DateTimeOffset oldStart, DateTimeOffset newStart, TimeZoneInfo zone)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"Dạ {bot} đã dời lịch của {khach} từ {CrmClock.FormatForCustomer(oldStart, zone)} sang {CrmClock.FormatForCustomer(newStart, zone)} ạ ✨";
    }

    public static string TooLate(string bot, string? salutation, int cancelLeadHours, string businessName)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"Dạ theo chính sách của {businessName}, đổi hay hủy lịch cần báo trước {cancelLeadHours} giờ ạ. {Bot(bot)} đã báo nhân viên hỗ trợ {khach} ngay nha.";
    }

    public static string NotFound(string bot, string? salutation)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"Dạ {bot} chưa thấy lịch sắp tới của {khach} trong hệ thống. {Bot(bot)} đã báo nhân viên kiểm tra giúp {khach} ạ.";
    }

    public static string NeedTime(string bot, string? salutation)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"{Cap(khach)} cho {bot} xin giờ cụ thể (ví dụ 15:00) để {bot} kiểm tra chỗ trống nha.";
    }

    public static string Reminder(string bot, string? salutation, DateTimeOffset startAt, TimeZoneInfo zone, string services, string branchName, string? address, bool tomorrow)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        var when = tomorrow ? "ngày mai" : "sắp tới";
        var place = string.IsNullOrWhiteSpace(address) ? branchName : $"{branchName} ({address})";
        return $"Dạ {bot} nhắc lịch {when} của {khach}: {CrmClock.FormatForCustomer(startAt, zone)}, {services}, tại {place} ạ. {Cap(khach)} nhắn \"ok\" xác nhận giúp {bot}, hoặc nhắn \"dời\"/\"hủy\" nếu cần thay đổi nha 🙏";
    }

    public static string WaitlistOffer(string bot, string? salutation, DateTimeOffset startAt, TimeZoneInfo zone, string service)
    {
        var khach = CustomerSalutation.Resolve(salutation);
        return $"Dạ {bot} báo tin vui: bên {bot} vừa có chỗ trống {CrmClock.FormatForCustomer(startAt, zone)} cho {service} ạ. {Cap(khach)} muốn {bot} giữ chỗ này không ạ?";
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Bot(string bot) => Cap(bot);
}
