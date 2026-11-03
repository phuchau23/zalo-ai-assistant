namespace ZaloAi.Core.Entities;

/// <summary>
/// Cài đặt chuyển tiếp bot ↔ nhân viên của một DN (docs/FEATURE-SPECS.md mục 1). Một dòng / tenant, tạo lười khi đọc lần đầu.
/// Biến trong câu: {xung_ho} {ten_doanh_nghiep} {ten_nhan_vien} {thoi_gian_phan_hoi} {gio_mo_cua_tiep_theo}.
/// </summary>
public sealed class HandoffSettings : ITenantOwned
{
    public const string DefaultHandoffMessage =
        "Dạ em xin phép chuyển {xung_ho} sang chuyên viên chăm sóc khách hàng của {ten_doanh_nghiep}. Chuyên viên sẽ phản hồi trong khoảng {thoi_gian_phan_hoi} ạ.";

    public const string DefaultAfterHoursMessage =
        "Dạ em xin phép chuyển {xung_ho} sang chuyên viên chăm sóc khách hàng của {ten_doanh_nghiep}. Hiện đã ngoài giờ làm việc, chuyên viên sẽ liên hệ lại {xung_ho} vào {gio_mo_cua_tiep_theo} ạ.";

    public const string DefaultTakeoverMessage =
        "Dạ chào {xung_ho}, em là {ten_nhan_vien}, chuyên viên chăm sóc khách hàng của {ten_doanh_nghiep}. Em sẽ hỗ trợ {xung_ho} tiếp ạ.";

    public const string DefaultReturnToBotMessage =
        "Dạ nếu {xung_ho} cần thêm thông tin, trợ lý AI của {ten_doanh_nghiep} luôn sẵn sàng hỗ trợ 24/7 ạ.";

    public Guid TenantId { get; set; }

    /// <summary>Câu bot gửi khi chuyển người trong giờ làm việc (luôn bật).</summary>
    public string HandoffMessage { get; set; } = DefaultHandoffMessage;

    /// <summary>Câu chuyển người ngoài giờ làm việc (luôn bật).</summary>
    public string AfterHoursMessage { get; set; } = DefaultAfterHoursMessage;

    public bool TakeoverMessageEnabled { get; set; } = true;

    public string TakeoverMessage { get; set; } = DefaultTakeoverMessage;

    public bool ReturnToBotMessageEnabled { get; set; }

    public string ReturnToBotMessage { get; set; } = DefaultReturnToBotMessage;

    /// <summary>Chữ ký cuối tin nhân viên: "— {tên}, CSKH".</summary>
    public bool StaffSignatureEnabled { get; set; } = true;

    /// <summary>Thời gian phản hồi dự kiến, chèn vào câu chuyển người (ví dụ "15 phút").</summary>
    public string ResponseTime { get; set; } = "15 phút";

    /// <summary>Giờ làm việc theo giờ Việt Nam, "HH:mm".</summary>
    public string OpenTime { get; set; } = "08:00";

    public string CloseTime { get; set; } = "21:00";

    /// <summary>Ngày làm việc, bit 0 = Chủ nhật ... bit 6 = Thứ 7 (mặc định cả tuần).</summary>
    public int WorkingDays { get; set; } = 0b111_1111;

    /// <summary>Hội thoại cần người quá số phút này chưa ai trả lời → nhắc lại (hộp thư + Telegram).</summary>
    public int ReminderMinutes { get; set; } = 10;

    /// <summary>Chat id Telegram (nhóm nhân viên) nhận thông báo; trống = không gửi Telegram.</summary>
    public string? TelegramChatId { get; set; }

    /// <summary>Bật AI gợi ý "Cần chăm sóc" (tắt để tiết kiệm chi phí AI).</summary>
    public bool CareEnabled { get; set; } = true;

    /// <summary>Khách im lặng quá số giờ này → AI xem có nên chủ động nhắn không (mặc định 6).</summary>
    public int CareColdHours { get; set; } = 6;

    /// <summary>true = bot tự nhắn chăm sóc (mặc định); false = chỉ gợi ý cho nhân viên duyệt.</summary>
    public bool CareAutoSend { get; set; } = true;

    /// <summary>Khung giờ bot được chủ động nhắn (giờ Việt Nam, "HH:mm"). Hệ thống chặn cứng ngoài 07:00–21:00.</summary>
    public string CareSendStart { get; set; } = "08:00";

    public string CareSendEnd { get; set; } = "20:00";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
