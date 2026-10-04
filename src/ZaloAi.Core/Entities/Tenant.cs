namespace ZaloAi.Core.Entities;

/// <summary>Phong cách trả lời của bot (trang Giọng văn).</summary>
public enum BotTone
{
    /// <summary>Thân thiện, ấm áp.</summary>
    Friendly = 0,

    /// <summary>Lịch sự, chuyên nghiệp.</summary>
    Professional = 1,

    /// <summary>Ngắn gọn, đi thẳng vào ý.</summary>
    Concise = 2,
}

public enum TenantStatus
{
    Active = 0,
    Suspended = 1,
    Cancelled = 2,
}

/// <summary>Doanh nghiệp thuê dịch vụ. Bảng gốc của cô lập dữ liệu: DbContext chỉ cho thấy tenant hiện tại.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Slug mẫu ngành trong ZaloAi.IndustryTemplates, ví dụ "spa".</summary>
    public required string IndustrySlug { get; set; }

    public required string BotName { get; set; }

    /// <summary>Cách bot xưng hô, ví dụ "em".</summary>
    public required string BotPronoun { get; set; }

    /// <summary>Link chính sách bảo mật của DN, chèn vào tin chào đầu tiên.</summary>
    public string? PrivacyUrl { get; set; }

    /// <summary>Gói thuê. Danh sách gói chốt khi định giá (tuần 7–8).</summary>
    public string Plan { get; set; } = "trial";

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public BotTone BotTone { get; set; } = BotTone.Friendly;

    /// <summary>
    /// Ghi chú thêm của chủ DN cho bot (ví dụ "luôn mời khách đặt lịch"). Không ghi đè được quy tắc an toàn và câu báo trợ lý AI.
    /// </summary>
    public string? BotInstructions { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
