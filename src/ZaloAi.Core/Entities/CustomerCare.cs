namespace ZaloAi.Core.Entities;

public enum ContactNoteKind
{
    /// <summary>Ghi chú chung.</summary>
    Note = 0,

    /// <summary>Khách đã dùng dịch vụ / mua hàng.</summary>
    Service = 1,

    /// <summary>Lịch hẹn (tái khám, buổi tiếp theo...).</summary>
    Appointment = 2,
}

/// <summary>
/// Nhân viên ghi lại khách đã làm gì ("đã làm massage 90 phút, hẹn tái khám 12/10"). Là căn cứ cho AI gợi ý chăm sóc (M6) và flow
/// tự động (GĐ2). Nội dung MÃ HÓA (có thể chứa thông tin cá nhân/sức khỏe).
/// </summary>
public sealed class ContactNote : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContactId { get; set; }

    public Guid? AuthorUserId { get; set; }

    public ContactNoteKind Kind { get; set; }

    public required string ContentEnc { get; set; }

    /// <summary>Ngày việc đó xảy ra (giờ Việt Nam, chỉ ngày).</summary>
    public DateOnly HappenedOn { get; set; }

    /// <summary>Lúc cần chăm sóc lại (hẹn tái khám, hỏi thăm...). Tới giờ → vào trang "Cần chăm sóc" (job quét mỗi phút).</summary>
    public DateTimeOffset? FollowUpAt { get; set; }

    /// <summary>Đã tạo gợi ý chăm sóc cho ngày hẹn này (không tạo lại).</summary>
    public DateTimeOffset? FollowUpQueuedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum CareTemperature
{
    Cold = 0,
    Warm = 1,
    Hot = 2,
}

public enum CareSuggestionStatus
{
    Open = 0,
    Done = 1,
    Skipped = 2,

    /// <summary>Quá hạn nhắn qua kênh mà chưa xử lý.</summary>
    Expired = 3,

    /// <summary>Bot đã tự nhắn khách.</summary>
    AutoSent = 4,
}

/// <summary>
/// Gợi ý "Cần chăm sóc" (docs/FEATURE-SPECS.md mục 2): khách nhân viên nên chủ động nhắn. Nhân viên sửa tin nháp rồi mới gửi —
/// hệ thống KHÔNG tự gửi. Lý do / gợi ý / tin nháp MÃ HÓA (tóm tắt nội dung hội thoại).
/// </summary>
public sealed class CareSuggestion : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContactId { get; set; }

    public Guid ConversationId { get; set; }

    public CareTemperature Temperature { get; set; }

    /// <summary>price_no_close | thinking | asked_schedule | complaint_followup | win_back | unused_package | follow_up | other</summary>
    public required string Trigger { get; set; }

    public required string ReasonEnc { get; set; }

    public string? SuggestedActionEnc { get; set; }

    public string? DraftEnc { get; set; }

    /// <summary>Hạn còn nhắn được qua kênh (Zalo: 7 ngày sau tương tác cuối của khách). null = không giới hạn (chat thử).</summary>
    public DateTimeOffset? MessagingDeadline { get; set; }

    /// <summary>Bot sẽ tự nhắn lúc này (ngoài khung giờ thì dời tới giờ mở). null = không tự nhắn (chờ nhân viên).</summary>
    public DateTimeOffset? ScheduledSendAt { get; set; }

    /// <summary>Vì sao cần nhân viên thay vì bot tự nhắn (mã ngắn, xem CareDecision).</summary>
    public string? EscalationReason { get; set; }

    /// <summary>Tin bot đã tự gửi.</summary>
    public Guid? SentMessageId { get; set; }

    public Guid? AssignedUserId { get; set; }

    public CareSuggestionStatus Status { get; set; }

    /// <summary>booked | bought | declined | no_response | customer_replied | other</summary>
    public string? Outcome { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
