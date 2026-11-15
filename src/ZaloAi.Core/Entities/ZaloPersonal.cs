namespace ZaloAi.Core.Entities;

/// <summary>Tình trạng bộ nghe tin của một nick do sidecar báo về (connectors/zalo-personal/CONTRACT.md mục 4).</summary>
public enum ListenerState
{
    /// <summary>Vừa kết nối / vừa bấm kết nối lại, chờ sidecar báo.</summary>
    Starting,

    Connected,

    /// <summary>Rớt mạng, zca-js đang tự nối lại.</summary>
    Reconnecting,

    /// <summary>Đóng mã 3000/3003: chủ nick mở Zalo Web nơi khác hoặc bị đá phiên. Không tự giành lại.</summary>
    OpenedElsewhere,

    /// <summary>Đăng nhập bằng phiên đã lưu thất bại → quét QR lại.</summary>
    SessionExpired,

    ProxyFailing,

    Stopped,

    /// <summary>Quá lâu không thấy sidecar báo nhịp tim (sidecar tắt / treo). Hệ thống tự đánh dấu, không cần chủ DN làm gì.</summary>
    Stale,
}

/// <summary>
/// Phần riêng của một nick Zalo cá nhân (1–1 với <see cref="ChannelConnection"/>, xóa theo). Phiên đăng nhập zca-js
/// (cookie, imei, userAgent) mã hóa bằng IFieldEncryptor: ai có phiên là có toàn quyền nick → không bao giờ trả ra API, không log.
/// </summary>
public sealed class ZaloPersonalAccount : ITenantOwned
{
    public Guid ConnectionId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>JSON {cookie, imei, userAgent} đã mã hóa.</summary>
    public required string SessionEnc { get; set; }

    public DateTimeOffset SessionSavedAt { get; set; }

    public ListenerState ListenerState { get; set; } = ListenerState.Starting;

    /// <summary>Mã đóng kết nối gần nhất (vd 3000) — để hiện lý do.</summary>
    public int? LastStateCode { get; set; }

    public DateTimeOffset? ListenerStateChangedAt { get; set; }

    /// <summary>Lần gần nhất sidecar báo nick này đang chạy (nhịp tim mỗi phút).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Bạn bè Zalo nhắn tới có được coi là khách luôn không. Mặc định không: nick cá nhân lẫn chuyện gia đình, bạn bè → chỉ lưu tên,
    /// chủ nick tự đánh dấu ai là khách.
    /// </summary>
    public bool TreatFriendsAsCustomers { get; set; }

    /// <summary>Chủ nick nhắn tay trên điện thoại → bot tạm im bấy nhiêu phút ở hội thoại đó.</summary>
    public int StaffPauseMinutes { get; set; } = 30;

    /// <summary>Proxy IP riêng của nick (null = chưa gán; production bắt buộc có — ZaloPersonal:RequireProxy).</summary>
    public Guid? ProxyId { get; set; }

    public PacingMode PacingMode { get; set; } = PacingMode.Safe;

    /// <summary>Chế độ tùy chỉnh: số lời mời kết bạn / ngày (null = theo mặc định an toàn).</summary>
    public int? CustomInvitesPerDay { get; set; }

    /// <summary>Chế độ tùy chỉnh: số người mới nhắn lần đầu / ngày.</summary>
    public int? CustomNewContactsPerDay { get; set; }

    /// <summary>Chế độ tùy chỉnh: tổng tin / ngày.</summary>
    public int? CustomMessagesPerDay { get; set; }

    /// <summary>Phanh tự động theo phản hồi khách (luôn bật ở chế độ an toàn; tắt được ở chế độ tùy chỉnh).</summary>
    public bool AutoBrake { get; set; } = true;

    public OutreachLevel OutreachLevel { get; set; } = OutreachLevel.Normal;

    /// <summary>Lý do mức phanh hiện tại (mã ngắn, vd "reject_rate").</summary>
    public string? OutreachLevelReason { get; set; }

    public DateTimeOffset? OutreachLevelChangedAt { get; set; }

    /// <summary>Mức "Dừng nhắn trước" kéo dài tới mốc này (sau đó chủ DN mở lại được).</summary>
    public DateTimeOffset? OutreachPausedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PeerKind
{
    /// <summary>Khách: tin được lưu, bot trả lời.</summary>
    Customer,

    /// <summary>Bạn bè chưa phân loại: chỉ lưu tên + giờ, KHÔNG lưu nội dung, không gửi AI.</summary>
    Pending,

    /// <summary>Riêng tư (chủ nick đánh dấu): bỏ qua hẳn.</summary>
    Private,
}

/// <summary>
/// Người đã nhắn vào một nick Zalo cá nhân — để lọc chuyện riêng của chủ nick khỏi CRM và AI (docs/ZALO-PERSONAL-PLAN.md mục 6.2).
/// </summary>
public sealed class ZaloPeer : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ConnectionId { get; set; }

    /// <summary>uid Zalo của người nhắn.</summary>
    public required string ExternalUserId { get; set; }

    public string? DisplayName { get; set; }

    public bool IsFriend { get; set; }

    public PeerKind Kind { get; set; }

    /// <summary>Hồ sơ khách tương ứng (khi là khách).</summary>
    public Guid? ContactId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PacingMode
{
    /// <summary>An toàn (mặc định): hệ thống tự đặt số theo tuổi nick, phanh tự động luôn bật.</summary>
    Safe,

    /// <summary>Tùy chỉnh: chủ DN tự nhập số mỗi ngày (trong trần hệ thống), phanh tắt được.</summary>
    Custom,
}

public enum OutreachLevel
{
    Normal,

    /// <summary>Khách phản hồi kém → giảm một nửa lời mời / người mới.</summary>
    Slow,

    /// <summary>Dừng nhắn trước (vẫn trả lời khách tự nhắn tới).</summary>
    Paused,
}

public enum ProxyStatus
{
    Active,

    /// <summary>Kiểm tra hỏng liên tiếp → nick dùng proxy này dừng (không bao giờ đi thẳng bằng IP máy chủ).</summary>
    Failing,

    /// <summary>Ngừng dùng (không xóa được proxy từng gán nick, để giữ lịch sử).</summary>
    Retired,
}

/// <summary>
/// Proxy IP riêng cho nick Zalo cá nhân (docs/ZALO-PERSONAL-PLAN.md mục 8). BẢNG HỆ THỐNG, không thuộc tenant (như users): chỉ super
/// admin quản lý; DN không bao giờ thấy địa chỉ/mật khẩu proxy. Một proxy chỉ gán một nick (unique zalo_personal_accounts.proxy_id).
/// </summary>
public sealed class Proxy
{
    public Guid Id { get; set; }

    public required string Label { get; set; }

    /// <summary>URL đầy đủ http(s)://user:pass@host:port đã mã hóa.</summary>
    public required string CredentialsEnc { get; set; }

    /// <summary>HMAC của URL chuẩn hóa — chống nhập trùng mà không phải giải mã.</summary>
    public required string Fingerprint { get; set; }

    /// <summary>host:port (không có tài khoản) để super admin nhận ra proxy.</summary>
    public required string Endpoint { get; set; }

    public string? Country { get; set; }

    public string? ExitIp { get; set; }

    public ProxyStatus Status { get; set; } = ProxyStatus.Active;

    /// <summary>Số lần kiểm tra hỏng liên tiếp.</summary>
    public int FailCount { get; set; }

    public DateTimeOffset? FailingSince { get; set; }

    /// <summary>Vừa thả ra từ một nick → nghỉ tới mốc này mới gán nick khác.</summary>
    public DateTimeOffset? CoolingUntil { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
