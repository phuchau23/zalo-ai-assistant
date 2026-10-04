namespace ZaloAi.Core.Entities;

public enum ChannelKind
{
    /// <summary>Khung chat trong trang quản trị (chat thử) và sau này chat website.</summary>
    Webchat = 0,
    Zalo = 1,
}

/// <summary>Khách cuối nhắn tin cho doanh nghiệp qua một kênh. Không trùng (tenant, kênh, mã người dùng của kênh).</summary>
public sealed class Contact : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public ChannelKind Channel { get; set; }

    /// <summary>Mã người dùng do kênh cấp (Zalo user id; webchat: mã phiên).</summary>
    public required string ExternalUserId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Thông tin khách tự cung cấp (tên, SĐT, nhu cầu...) dạng JSON, MÃ HÓA (dữ liệu cá nhân).</summary>
    public string? LeadFieldsEnc { get; set; }

    /// <summary>Tầng khách tiềm năng. Hệ thống tự nâng (Mới → Quan tâm → Nóng); Đã chốt / Không tiềm năng chỉ nhân viên đặt.</summary>
    public LeadStatus LeadStatus { get; set; } = LeadStatus.New;

    /// <summary>Nhân viên đã tự đặt trạng thái → hệ thống không tự đổi nữa (cho tới khi chọn lại "tự động").</summary>
    public bool LeadStatusManual { get; set; }

    public DateTimeOffset? LeadStatusChangedAt { get; set; }

    /// <summary>Nhãn do nhân viên gắn (ví dụ "VIP", "khách cũ").</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Tin gần nhất của khách trên mọi hội thoại (lọc, sắp xếp danh sách khách).</summary>
    public DateTimeOffset? LastCustomerMessageAt { get; set; }

    /// <summary>Không cho bot chủ động nhắn khách này (khách nhắn "hủy"/"dừng", hoặc nhân viên tắt). null = được nhắn.</summary>
    public DateTimeOffset? ProactiveOptOutAt { get; set; }

    /// <summary>customer (khách tự từ chối — nhân viên KHÔNG được bật lại) | staff.</summary>
    public string? ProactiveOptOutSource { get; set; }

    /// <summary>Lần cuối bot chủ động nhắn (cách nhau tối thiểu 24 giờ).</summary>
    public DateTimeOffset? LastProactiveAt { get; set; }

    /// <summary>Bot đã chủ động nhắn và khách CHƯA trả lời → không nhắn tin chủ động thứ hai.</summary>
    public bool ProactiveAwaitingReply { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Tầng khách tiềm năng (số lớn hơn = "nóng" hơn khi tự nâng; Won/Lost chỉ nhân viên đặt).</summary>
public enum LeadStatus
{
    New = 0,
    Interested = 1,
    Hot = 2,

    /// <summary>Đã mua / đặt lịch.</summary>
    Won = 3,

    /// <summary>Không tiềm năng.</summary>
    Lost = 4,
}

public enum ConversationMode
{
    /// <summary>Bot tự trả lời.</summary>
    Bot = 0,

    /// <summary>Nhân viên tiếp quản; bot im lặng.</summary>
    Human = 1,
}

public enum ConversationStatus
{
    Open = 0,
    Closed = 1,
}

public enum Urgency
{
    None = 0,
    Normal = 1,

    /// <summary>Dấu hiệu nguy hiểm / khẩn cấp: phải có người xử lý ngay.</summary>
    Urgent = 2,
}

public sealed class Conversation : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContactId { get; set; }

    public ChannelKind Channel { get; set; }

    /// <summary>Kênh đã kết nối nhận tin (Zalo OA). null với webchat, hoặc khi DN đã ngắt kết nối kênh.</summary>
    public Guid? ConnectionId { get; set; }

    public ConversationMode Mode { get; set; } = ConversationMode.Bot;

    public ConversationStatus Status { get; set; } = ConversationStatus.Open;

    /// <summary>Hội thoại chat thử của chủ DN/nhân viên (không tính vào báo cáo, hạn mức).</summary>
    public bool IsTest { get; set; }

    /// <summary>Tóm tắt các tin cũ (tiết kiệm token), MÃ HÓA vì có thể chứa nội dung khách.</summary>
    public string? SummaryEnc { get; set; }

    /// <summary>Tin nhắn cuối cùng đã được gộp vào tóm tắt.</summary>
    public DateTimeOffset? SummarizedUntil { get; set; }

    /// <summary>Lý do chuyển nhân viên gần nhất (mã ngắn, không chứa nội dung khách).</summary>
    public string? HandoffReason { get; set; }

    public Urgency Urgency { get; set; }

    public DateTimeOffset? LastCustomerMessageAt { get; set; }

    /// <summary>Tin gần nhất (mọi phía) — sắp xếp hộp thư.</summary>
    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>Nhân viên phụ trách (tự gán khi tiếp quản / trả lời; owner gán lại được).</summary>
    public Guid? AssignedUserId { get; set; }

    /// <summary>Từ lúc nào khách đang chờ người trả lời (bot đã chuyển, khách nhắn khi đang ở chế độ nhân viên). null = không ai cần làm gì.</summary>
    public DateTimeOffset? NeedsAttentionSince { get; set; }

    /// <summary>Lần nhắc gần nhất cho ca đang chờ (tránh nhắc liên tục).</summary>
    public DateTimeOffset? LastReminderAt { get; set; }

    /// <summary>Lần AI phân tích "Cần chăm sóc" gần nhất — chỉ phân tích lại khi khách có tin mới sau mốc này.</summary>
    public DateTimeOffset? CareAnalyzedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MessageDirection
{
    In = 0,
    Out = 1,
}

/// <summary>Trạng thái gửi tin ra kênh (chỉ tin Out). Job chạy lại: Pending → gửi lại đúng tin đó, không sinh câu trả lời mới.</summary>
public enum DeliveryStatus
{
    /// <summary>Tin vào, hoặc kênh không cần gửi.</summary>
    None = 0,
    Pending = 1,
    Sent = 2,

    /// <summary>Lỗi vĩnh viễn (khách không còn nhận được, token hỏng...) — không gửi lại.</summary>
    Failed = 3,
}

public enum MessageSender
{
    Customer = 0,
    Bot = 1,
    Staff = 2,

    /// <summary>Câu chuyển tiếp hệ thống tự gửi (chuyển người, nhân viên tiếp quản, trả lại bot).</summary>
    System = 3,
}

public sealed class Message : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ConversationId { get; set; }

    public MessageDirection Direction { get; set; }

    public MessageSender Sender { get; set; }

    /// <summary>Nội dung tin, MÃ HÓA (AES-256-GCM).</summary>
    public required string ContentEnc { get; set; }

    /// <summary>Mã tin của kênh (Zalo message id) — chống xử lý trùng khi kênh gửi lại.</summary>
    public string? ExternalMessageId { get; set; }

    /// <summary>Tin bot trả lời cho tin khách nào. Không trùng → xử lý lại cùng một job không trả lời 2 lần.</summary>
    public Guid? ReplyToMessageId { get; set; }

    /// <summary>
    /// Dấu vết AI (jsonb): model, phiên bản prompt, đoạn dữ liệu đã dùng, độ tự tin, lý do chuyển người, chi phí...
    /// KHÔNG chứa nội dung tin hay dữ liệu cá nhân (nội dung nằm ở ContentEnc).
    /// </summary>
    public string? AiTraceJson { get; set; }

    public DeliveryStatus DeliveryStatus { get; set; }

    /// <summary>Tin bot CHỦ ĐỘNG nhắn (chăm sóc), không phải trả lời khách.</summary>
    public bool Proactive { get; set; }

    /// <summary>Nhân viên gửi tin (Sender = Staff, hoặc câu tiếp quản của nhân viên).</summary>
    public Guid? SenderUserId { get; set; }

    /// <summary>Mã lỗi gửi gần nhất (ví dụ "zalo:-230"), không chứa nội dung.</summary>
    public string? DeliveryError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum UsageKind
{
    Chat = 0,
    Embed = 1,
}

/// <summary>Một lần gọi AI có tính tiền: để tính chi phí từng doanh nghiệp, từng hội thoại.</summary>
public sealed class UsageRecord : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public UsageKind Kind { get; set; }

    public required string Provider { get; set; }

    public required string Model { get; set; }

    public int InputTokens { get; set; }

    /// <summary>Gồm cả token "suy nghĩ" của model (tính tiền như output).</summary>
    public int OutputTokens { get; set; }

    public decimal CostUsd { get; set; }

    public Guid? ConversationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
