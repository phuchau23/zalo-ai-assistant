using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Cấu hình AI. Chat dùng từ M3; embedding dùng từ M2.</summary>
public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "Ai";

    /// <summary>Số chiều cột chunks.embedding trong database. Đổi = migration + đánh chỉ mục lại (skill db-migration).</summary>
    public const int SupportedEmbedDimensions = 768;

    [Required, RegularExpression("^(gemini|anthropic)$")]
    public string ChatProvider { get; set; } = "gemini";

    public string? ChatModel { get; set; }

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

    public string? AnthropicApiKey { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EmbedDim != SupportedEmbedDimensions)
        {
            yield return new ValidationResult(
                $"{nameof(EmbedDim)} phải bằng {SupportedEmbedDimensions} (số chiều cột vector trong database).",
                [nameof(EmbedDim)]);
        }

        if (EmbedProvider == "gemini" && string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            yield return new ValidationResult(
                $"{nameof(GeminiApiKey)} bắt buộc khi {nameof(EmbedProvider)} = gemini (đặt bằng user-secrets \"Ai:GeminiApiKey\" hoặc env Ai__GeminiApiKey).",
                [nameof(GeminiApiKey)]);
        }
    }
}
