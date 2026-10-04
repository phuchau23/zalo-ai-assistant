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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MessageDirection
{
    In = 0,
    Out = 1,
}

public enum MessageSender
{
    Customer = 0,
    Bot = 1,
    Staff = 2,
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
