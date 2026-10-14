namespace ZaloAi.Core.Entities;

/// <summary>
/// Cách bot gọi khách (chủ dự án chốt 2026-10-14): biết giới tính → "anh"/"chị" (nhân viên có thể chọn cô/chú/bác),
/// chưa biết → "mình". Không gọi trống tên, không viết dạng "anh/chị". Bot xưng theo cài đặt DN (mặc định "em").
/// </summary>
public static class CustomerSalutation
{
    public const string Unknown = "mình";

    public const string SourceStaff = "staff";

    public const string SourceCustomer = "customer";

    public static readonly IReadOnlyList<string> Allowed = ["anh", "chị", "cô", "chú", "bác"];

    public static bool IsAllowed(string? value) => value is not null && Allowed.Contains(value, StringComparer.Ordinal);

    /// <summary>Cách gọi dùng khi nhắn: giá trị đã lưu nếu hợp lệ, không thì "mình".</summary>
    public static string Resolve(string? value) => IsAllowed(value) ? value! : Unknown;
}
