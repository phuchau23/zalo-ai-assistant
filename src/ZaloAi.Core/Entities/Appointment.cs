namespace ZaloAi.Core.Entities;

public enum AppointmentStatus
{
    /// <summary>Đã đặt, chưa xác nhận.</summary>
    Booked = 0,

    /// <summary>Khách đã xác nhận (trả lời tin nhắc).</summary>
    Confirmed = 1,

    /// <summary>Khách đã đến (check-in).</summary>
    Arrived = 2,

    InProgress = 3,

    /// <summary>Đã làm xong — mốc để chăm sóc sau buổi, trừ buổi gói, ghi doanh thu (C3).</summary>
    Done = 4,

    /// <summary>Khách không đến.</summary>
    NoShow = 5,

    Cancelled = 6,
}

public enum AppointmentSource
{
    /// <summary>Bot tự chốt qua chat.</summary>
    Bot = 0,

    /// <summary>Nhân viên đặt tay (khách gọi điện, nhắn).</summary>
    Staff = 1,

    /// <summary>Trang đặt lịch online (C8).</summary>
    Online = 2,

    /// <summary>Khách đến trực tiếp.</summary>
    WalkIn = 3,
}

public enum ResourceKind
{
    Bed = 0,
    Room = 1,
    Device = 2,
}

public enum WaitlistStatus
{
    Waiting = 0,

    /// <summary>Đã nhắn khách báo có chỗ.</summary>
    Offered = 1,

    Booked = 2,

    Cancelled = 3,
}

/// <summary>Giường / phòng / máy của một chi nhánh (CRM C2). Có tài nguyên → số lịch cùng lúc không vượt số tài nguyên đang hoạt động.</summary>
public sealed class Resource : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public required string Name { get; set; }

    public ResourceKind Kind { get; set; } = ResourceKind.Bed;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Lịch hẹn (CRM C2). Giờ lưu UTC; hiển thị theo múi giờ của DN. Ghi chú và tóm tắt AI về khách MÃ HÓA (có thể chứa sức khỏe).
/// Bot chỉ đề xuất; <c>AppointmentService</c> kiểm tra chỗ trống rồi mới tạo (CLAUDE.md: AI chỉ đề xuất, hệ thống mới chốt).
/// </summary>
public sealed class Appointment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid ContactId { get; set; }

    /// <summary>Nhân viên thực hiện; null = chưa xếp người (hệ thống chọn khi có chỗ).</summary>
    public Guid? StaffMemberId { get; set; }

    public Guid? ResourceId { get; set; }

    public DateTimeOffset StartAt { get; set; }

    public DateTimeOffset EndAt { get; set; }

    /// <summary>Phút dọn dẹp sau lịch (từ dịch vụ), tính vào chỗ trống nhưng không hiện cho khách.</summary>
    public int BufferMinutes { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;

    public AppointmentSource Source { get; set; }

    public Guid? BookedByUserId { get; set; }

    /// <summary>Hội thoại bot đã chốt lịch (để nhắc lịch, đổi/hủy qua chat).</summary>
    public Guid? ConversationId { get; set; }

    public Guid? SeriesId { get; set; }

    /// <summary>Số khách (đặt nhóm).</summary>
    public int PartySize { get; set; } = 1;

    public string? NoteEnc { get; set; }

    /// <summary>Tóm tắt AI cho kỹ thuật viên: khách gặp gì, muốn gì, lưu ý, buổi trước. MÃ HÓA.</summary>
    public string? AiBriefEnc { get; set; }

    public DateTimeOffset? AiBriefAt { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>customer | staff | bot | system</summary>
    public string? CancelledBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public DateTimeOffset? Reminder1SentAt { get; set; }

    public DateTimeOffset? Reminder2SentAt { get; set; }

    public DateTimeOffset? CustomerConfirmedAt { get; set; }

    public DateTimeOffset? ArrivedAt { get; set; }

    public DateTimeOffset? DoneAt { get; set; }

    /// <summary>Đã tạo gợi ý chăm sóc sau buổi (C7) — không tạo lại.</summary>
    public DateTimeOffset? AfterCareQueuedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsActive => Status is not (AppointmentStatus.Cancelled or AppointmentStatus.NoShow);
}

/// <summary>Dịch vụ trong lịch (ảnh chụp tên/thời lượng/giá tại lúc đặt để lịch sử không đổi khi sửa danh mục).</summary>
public sealed class AppointmentItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid AppointmentId { get; set; }

    public Guid CatalogItemId { get; set; }

    public required string Name { get; set; }

    public int DurationMinutes { get; set; }

    public long? PriceSnapshot { get; set; }

    public int Position { get; set; }
}

/// <summary>Nhật ký lịch hẹn: tạo, dời, hủy, đổi trạng thái, nhắc. Ai làm (người / bot / hệ thống). Dữ liệu không chứa PII.</summary>
public sealed class AppointmentEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid AppointmentId { get; set; }

    /// <summary>created | rescheduled | cancelled | status_changed | reminder_sent | confirmed | brief_written | assigned</summary>
    public required string Kind { get; set; }

    public Guid? ByUserId { get; set; }

    /// <summary>staff | bot | customer | system</summary>
    public required string Actor { get; set; }

    public string? DataJson { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>Lịch định kỳ: N buổi cách nhau <see cref="IntervalDays"/> ngày (khách làm gói hằng tuần).</summary>
public sealed class AppointmentSeries : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid ContactId { get; set; }

    public int IntervalDays { get; set; } = 7;

    public int Occurrences { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Hàng chờ khi khung giờ kín: có chỗ trống (ai đó hủy) → bot nhắn khách (C2/C7).</summary>
public sealed class WaitlistEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid ContactId { get; set; }

    public Guid CatalogItemId { get; set; }

    public Guid? StaffMemberId { get; set; }

    public DateTimeOffset PreferredFrom { get; set; }

    public DateTimeOffset PreferredTo { get; set; }

    public WaitlistStatus Status { get; set; } = WaitlistStatus.Waiting;

    public string? NoteEnc { get; set; }

    public DateTimeOffset? OfferedAt { get; set; }

    public DateTimeOffset? OfferedSlotAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
