namespace ZaloAi.Core.Entities;

/// <summary>Cài đặt CRM của một doanh nghiệp (khóa chính = tenant_id, tạo lười khi cần như <see cref="HandoffSettings"/>).</summary>
public sealed class CrmSettings : ITenantOwned
{
    public const string DefaultTimeZone = "Asia/Ho_Chi_Minh";
    public const int DefaultSlotMinutes = 30;
    public const int DefaultDiscountApprovalPercent = 20;

    public Guid TenantId { get; set; }

    /// <summary>Múi giờ IANA dùng cho lịch hẹn, báo cáo theo ngày.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    /// <summary>Bước khung giờ trên lịch (15 / 30 / 60 phút).</summary>
    public int SlotMinutes { get; set; } = DefaultSlotMinutes;

    /// <summary>Nhân viên giảm giá quá % này cần chủ duyệt (C3).</summary>
    public int DiscountApprovalPercent { get; set; } = DefaultDiscountApprovalPercent;

    /// <summary>Phút dọn dẹp mặc định giữa hai khách khi dịch vụ không đặt riêng.</summary>
    public int DefaultBufferMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
