using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

public enum ZaloPersonalMode
{
    /// <summary>Sidecar chạy zca-js với Zalo thật.</summary>
    Real,

    /// <summary>Sidecar giả lập (chỉ Development): làm và test mọi luồng khi chưa có nick thật.</summary>
    Fake,
}

/// <summary>
/// Kênh Zalo cá nhân qua sidecar zca-js (docs/ZALO-PERSONAL-PLAN.md, connectors/zalo-personal/CONTRACT.md).
/// SharedSecret chỉ đặt qua user-secrets/env. Chưa cấu hình → ứng dụng vẫn chạy, nút kết nối Zalo cá nhân báo "chưa cấu hình".
/// Mode=Fake ngoài môi trường Development → không khởi động (kiểm ở ZaloPersonalOptionsValidator).
/// </summary>
public sealed class ZaloPersonalOptions : IValidatableObject
{
    public const string SectionName = "ZaloPersonal";

    /// <summary>Địa chỉ sidecar trong mạng nội bộ (dev: http://127.0.0.1:4100). Không bao giờ để sidecar ra internet.</summary>
    [Url]
    public string? SidecarUrl { get; set; }

    /// <summary>Khóa HMAC ký mọi request hai chiều API ⇄ sidecar (≥ 32 ký tự, giống REMI_SHARED_SECRET của sidecar).</summary>
    public string? SharedSecret { get; set; }

    public ZaloPersonalMode Mode { get; set; } = ZaloPersonalMode.Real;

    /// <summary>Cổng riêng cho /internal/zalo-personal/* — chỉ sidecar gọi, không đi qua tunnel / reverse proxy công khai.</summary>
    [Range(1, 65535)]
    public int InternalPort { get; set; } = 4001;

    [Range(1, 20)]
    public int MaxAccountsPerTenant { get; set; } = 2;

    /// <summary>Gom tin: chờ khách im bấy nhiêu giây rồi mới trả lời một lần (job đến sau thắng).</summary>
    [Range(0, 60)]
    public int DebounceSeconds { get; set; } = 8;

    /// <summary>Độ trễ "đang soạn" trước khi bot gửi, để giống người thật (kẹp trong khoảng này).</summary>
    [Range(0, 15000)]
    public int ReplyDelayMinMs { get; set; } = 3000;

    [Range(0, 15000)]
    public int ReplyDelayMaxMs { get; set; } = 12000;

    /// <summary>Số tin tối đa mỗi phút qua một nick (vượt → job gửi lại phút sau).</summary>
    [Range(1, 60)]
    public int SendPerMinute { get; set; } = 6;

    /// <summary>
    /// Thời gian chờ một lệnh gửi sidecar: độ trễ "đang soạn" (≤ 15 giây) + chờ nick kết nối xong + Zalo trả lời. Sidecar khuyến nghị ≥ 60.
    /// </summary>
    [Range(5, 180)]
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Quá số phút này không thấy nhịp tim của nick → đánh dấu "mất liên lạc với máy chủ kết nối".</summary>
    [Range(2, 60)]
    public int StaleAfterMinutes { get; set; } = 3;

    /// <summary>Bắt buộc mỗi nick có proxy IP riêng (production: true). Không có proxy trống → không cho kết nối nick.</summary>
    public bool RequireProxy { get; set; }

    /// <summary>Trần hệ thống — DN không vượt được ở chế độ nào (docs/ZALO-PERSONAL-PLAN.md mục 7).</summary>
    [Range(0, 200)]
    public int MaxInvitesPerDay { get; set; } = 20;

    [Range(0, 500)]
    public int MaxNewContactsPerDay { get; set; } = 40;

    [Range(1, 5000)]
    public int MaxMessagesPerDay { get; set; } = 300;

    /// <summary>Số tin tối đa mỗi giờ qua một nick (vượt: khách nhắn tới được chuyển nhân viên, tin chủ động dời).</summary>
    [Range(1, 1000)]
    public int MessagesPerHour { get; set; } = 60;

    /// <summary>Số lần tra SĐT → nick Zalo mỗi ngày.</summary>
    [Range(0, 500)]
    public int FindUserPerDay { get; set; } = 30;

    /// <summary>
    /// Ban đêm (giờ Việt Nam, "HH:mm", có thể qua nửa đêm): bot vẫn trả lời khách nhắn tới nhưng chậm như người thật — gom tin lâu hơn,
    /// "đang soạn" lâu hơn, 1 tin mỗi lượt (chủ dự án chốt 2026-11-15). Tin chủ động vẫn theo khung giờ chăm sóc.
    /// </summary>
    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string NightStart { get; set; } = "22:00";

    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string NightEnd { get; set; } = "07:00";

    [Range(0, 600)]
    public int NightDebounceSeconds { get; set; } = 90;

    /// <summary>Nhân thời gian "đang soạn" ban đêm (sidecar vẫn kẹp tối đa 15 giây).</summary>
    [Range(1.0, 5.0)]
    public double NightTypingFactor { get; set; } = 2.0;

    /// <summary>Thả proxy khỏi một nick → nghỉ bấy nhiêu ngày mới gán nick khác.</summary>
    [Range(0, 60)]
    public int ProxyCooldownDays { get; set; } = 7;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SidecarUrl) && !string.IsNullOrWhiteSpace(SharedSecret);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(SharedSecret) && SharedSecret.Trim().Length < 32)
        {
            yield return new ValidationResult("ZaloPersonal:SharedSecret phải dài ít nhất 32 ký tự.", [nameof(SharedSecret)]);
        }

        if (ReplyDelayMinMs > ReplyDelayMaxMs)
        {
            yield return new ValidationResult("ZaloPersonal:ReplyDelayMinMs không được lớn hơn ReplyDelayMaxMs.", [nameof(ReplyDelayMinMs)]);
        }
    }
}
