using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Giá một model, USD trên 1 triệu token (cập nhật tay theo trang giá của nhà cung cấp).</summary>
public sealed class ModelPrice
{
    [Range(0, 1000)]
    public decimal InputPerMillionUsd { get; set; }

    [Range(0, 1000)]
    public decimal OutputPerMillionUsd { get; set; }
}

/// <summary>
/// Cấu hình AI. Chỉ dùng Gemini (chủ dự án chốt 2026-10-04, bỏ Claude). Production phải dùng gói Gemini TRẢ PHÍ:
/// gói miễn phí cho phép Google dùng dữ liệu để cải thiện sản phẩm.
/// </summary>
public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "Ai";

    /// <summary>Số chiều cột chunks.embedding trong database. Đổi = migration + đánh chỉ mục lại (skill db-migration).</summary>
    public const int SupportedEmbedDimensions = 768;

    /// <summary>gemini: gọi Google. fake: trả lời giả tính tại chỗ (test, chạy không mạng) — không dùng ở production.</summary>
    [Required, RegularExpression("^(gemini|fake)$")]
    public string ChatProvider { get; set; } = "gemini";

    /// <summary>Kiểm chứng 2026-10-04: https://ai.google.dev/gemini-api/docs/models</summary>
    [Required]
    public string ChatModel { get; set; } = "gemini-3.8-flash";

    /// <summary>Model dự phòng khi model chính quá tải (503) hoặc hết hạn mức (429) sau khi đã thử lại. Để trống = không dùng.</summary>
    public string? ChatFallbackModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>gemini: gọi Google. fake: vector giả tính tại chỗ (test, chạy không mạng) — không dùng ở production.</summary>
    [Required, RegularExpression("^(gemini|fake)$")]
    public string EmbedProvider { get; set; } = "gemini";

    /// <summary>Kiểm chứng 2026-10-03: https://ai.google.dev/gemini-api/docs/embeddings</summary>
    [Required]
    public string EmbedModel { get; set; } = "gemini-embedding-2";

    public int EmbedDim { get; set; } = SupportedEmbedDimensions;

    /// <summary>Số đoạn gửi trong một lần gọi tạo vector.</summary>
    [Range(1, 100)]
    public int EmbedBatchSize { get; set; } = 50;

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Url]
    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    public string? GeminiApiKey { get; set; }

    /// <summary>Số đoạn kiến thức đưa vào mỗi lần trả lời.</summary>
    [Range(1, 20)]
    public int BotSearchLimit { get; set; } = 6;

    /// <summary>Độ giống tối thiểu (0..1) để một đoạn được đưa cho AI.</summary>
    [Range(0.0, 1.0)]
    public double BotMinScore { get; set; } = 0.35;

    /// <summary>Số tin gần nhất gửi kèm nguyên văn; tin cũ hơn được tóm tắt.</summary>
    [Range(2, 40)]
    public int BotHistoryTurns { get; set; } = 10;

    /// <summary>Bảng giá theo tên model (appsettings.json, section Ai:Pricing). Model không có giá → chi phí ghi 0.</summary>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal Cost(string model, int inputTokens, int outputTokens) =>
        Pricing.TryGetValue(model, out var price)
            ? ((inputTokens * price.InputPerMillionUsd) + (outputTokens * price.OutputPerMillionUsd)) / 1_000_000m
            : 0m;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EmbedDim != SupportedEmbedDimensions)
        {
            yield return new ValidationResult(
                $"{nameof(EmbedDim)} phải bằng {SupportedEmbedDimensions} (số chiều cột vector trong database).",
                [nameof(EmbedDim)]);
        }

        if ((EmbedProvider == "gemini" || ChatProvider == "gemini") && string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            yield return new ValidationResult(
                $"{nameof(GeminiApiKey)} bắt buộc khi dùng Gemini (đặt bằng user-secrets \"Ai:GeminiApiKey\" hoặc env Ai__GeminiApiKey).",
                [nameof(GeminiApiKey)]);
        }
    }
}
