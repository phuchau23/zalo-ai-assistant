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

    // Đặt lịch (C2)

    /// <summary>Bot được tự chốt lịch qua chat (tắt → bot chỉ ghi nhận, chuyển nhân viên như trước).</summary>
    public bool BotBookingEnabled { get; set; } = true;

    /// <summary>Khách đặt nhóm từ số người này trở lên → bot không tự chốt, chuyển nhân viên.</summary>
    public int BotMaxPartySize { get; set; } = 2;

    /// <summary>Phải đặt trước ít nhất bao nhiêu phút so với giờ bắt đầu.</summary>
    public int BookingLeadMinutes { get; set; } = 60;

    /// <summary>Đặt xa nhất bao nhiêu ngày.</summary>
    public int MaxBookingDaysAhead { get; set; } = 60;

    /// <summary>Khách tự hủy/dời qua chat phải trước giờ hẹn ít nhất bấy nhiêu giờ (theo chính sách DN).</summary>
    public int CancelLeadHours { get; set; } = 2;

    /// <summary>Nhắc lịch lần 1 trước giờ hẹn (giờ); 0 = tắt.</summary>
    public int Reminder1Hours { get; set; } = 24;

    /// <summary>Nhắc lịch lần 2 trước giờ hẹn (giờ); 0 = tắt.</summary>
    public int Reminder2Hours { get; set; } = 2;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
