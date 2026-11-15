namespace ZaloAi.Core.Entities;

public enum ConnectionStatus
{
    /// <summary>Đang hoạt động: nhận và trả lời tin.</summary>
    Active,

    /// <summary>Token hỏng / OA thu hồi quyền (Zalo cá nhân: phiên đăng nhập hết hạn → quét QR lại). Bot dừng trả lời.</summary>
    NeedsReauth,

    /// <summary>
    /// Zalo cá nhân: mất kết nối cần chủ DN xử lý (mở Zalo Web nơi khác, proxy hỏng). Phiên vẫn giữ, bấm "Kết nối lại" là chạy tiếp.
    /// Bot dừng trả lời.
    /// </summary>
    Disconnected,
}

/// <summary>
/// Kênh của DN đã kết nối: Zalo OA, Zalo cá nhân. Một OA / một nick chỉ thuộc một DN tại một thời điểm (unique channel + external_id
/// toàn hệ thống), để sự kiện vào tìm đúng DN. Token mã hóa bằng IFieldEncryptor, không bao giờ trả ra API hay ghi log.
/// Zalo cá nhân: 4 cột token OA để rỗng / bằng lúc tạo (không dùng); phiên đăng nhập nằm ở <see cref="ZaloPersonalAccount"/>.
/// Ngắt kết nối = xóa dòng này (hội thoại cũ giữ lại, connection_id về null).
/// </summary>
public sealed class ChannelConnection : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public ChannelKind Channel { get; set; }

    /// <summary>Zalo: OA ID. Zalo cá nhân: uid của nick.</summary>
    public required string ExternalId { get; set; }

    /// <summary>Tên hiển thị (tên OA) — để trống thì giao diện hiện ExternalId.</summary>
    public string? Name { get; set; }

    public required string AccessTokenEnc { get; set; }

    public required string RefreshTokenEnc { get; set; }

    public DateTimeOffset AccessTokenExpiresAt { get; set; }

    /// <summary>Hạn refresh token (Zalo: 3 tháng kể từ lần cấp mới nhất).</summary>
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }

    public ConnectionStatus Status { get; set; } = ConnectionStatus.Active;

    /// <summary>Mã lỗi gần nhất (ví dụ "zalo:-220"), không chứa token hay nội dung.</summary>
    public string? LastError { get; set; }

    public Guid? ConnectedByUserId { get; set; }

    public DateTimeOffset? LastRefreshedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
