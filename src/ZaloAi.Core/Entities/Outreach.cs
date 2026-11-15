namespace ZaloAi.Core.Entities;

/// <summary>Trạng thái một người trong hàng đợi chăm sóc qua Zalo cá nhân (docs/ZALO-PERSONAL-PLAN.md mục 6.7–6.9).</summary>
public enum OutreachState
{
    /// <summary>Chờ tới lượt (tìm Zalo theo SĐT, nhắn tin đầu hoặc mời kết bạn).</summary>
    Queued,

    /// <summary>Không có nick Zalo nào đăng ký SĐT này → nhân viên gọi điện.</summary>
    NoZalo,

    /// <summary>Đã gửi lời mời kết bạn kèm lời chào, chờ chấp nhận (tối đa 14 ngày).</summary>
    Invited,

    /// <summary>Đã kết bạn, chờ lượt gửi tin đầu.</summary>
    Friend,

    /// <summary>Đã gửi tin đầu, chờ khách trả lời.</summary>
    Messaged,

    /// <summary>Khách đã trả lời — bot tư vấn như khách bình thường.</summary>
    Replied,

    /// <summary>Khách từ chối / chặn / "hủy" → không bao giờ liên hệ lại.</summary>
    Declined,

    /// <summary>14 ngày không chấp nhận kết bạn → đã rút lời mời, thôi.</summary>
    Expired,

    /// <summary>7 ngày sau tin đầu vẫn im → vào danh sách gọi điện (nhân viên quyết định).</summary>
    NoReply,

    /// <summary>DN dừng chăm sóc người này / người này đã đang chat với DN.</summary>
    Stopped,

    /// <summary>Lỗi không gửi được (ghi LastError).</summary>
    Failed,
}

/// <summary>Một lần DN dán SĐT hàng loạt (ai dán, nguồn, xác nhận nguồn lúc nào).</summary>
public sealed class OutreachBatch : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Nick phụ trách (null = nick mặc định lúc dán).</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>referral | inquiry | customer</summary>
    public required string Origin { get; set; }

    /// <summary>Ghi chú cho AI của cả đợt (mã hóa).</summary>
    public string? NoteEnc { get; set; }

    public Guid? ReferrerContactId { get; set; }

    /// <summary>Tên người giới thiệu do DN nhập (nhắc trong tin đầu).</summary>
    public string? ReferrerName { get; set; }

    /// <summary>Người đã bấm xác nhận "các số này là người đã liên hệ / dùng dịch vụ / được giới thiệu".</summary>
    public Guid? AttestedByUserId { get; set; }

    public DateTimeOffset AttestedAt { get; set; }

    public int AcceptedCount { get; set; }

    public int SkippedCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Một người trong hàng đợi chăm sóc: điều khiển job đi tìm / mời / nhắn và màn hình "Đang chăm sóc".</summary>
public sealed class OutreachTarget : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid BatchId { get; set; }

    public Guid ContactId { get; set; }

    /// <summary>Nick Zalo cá nhân phụ trách.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>Nhân viên phụ trách.</summary>
    public Guid? AssignedUserId { get; set; }

    public OutreachState State { get; set; } = OutreachState.Queued;

    /// <summary>Lúc job xử lý tiếp (null = không cần làm gì nữa).</summary>
    public DateTimeOffset? NextActionAt { get; set; }

    /// <summary>uid Zalo tìm được theo SĐT.</summary>
    public string? ZaloUid { get; set; }

    public DateTimeOffset? FriendRequestSentAt { get; set; }

    public DateTimeOffset? FirstMessageAt { get; set; }

    public DateTimeOffset? RepliedAt { get; set; }

    /// <summary>Mã lỗi ngắn (không chứa nội dung).</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Tin nhắn mẫu nhân viên chèn nhanh trong hộp thư (gõ "/" + phím tắt). Nội dung là câu chữ chung của DN (không chứa dữ liệu khách) nên
/// không mã hóa. Chủ DN tạo/sửa/xóa, nhân viên dùng.
/// </summary>
public sealed class QuickReply : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Phím tắt (chữ thường, số, '-', '_'), duy nhất trong DN — vd "gia", "dia-chi".</summary>
    public required string Shortcut { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public int SortOrder { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
