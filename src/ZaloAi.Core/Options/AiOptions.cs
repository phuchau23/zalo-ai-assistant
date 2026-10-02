using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Model và key bắt buộc từ M3; hiện để trống được.</summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    [Required, RegularExpression("^(gemini|anthropic)$")]
    public string ChatProvider { get; set; } = "gemini";

    public string? ChatModel { get; set; }

    [Required, RegularExpression("^(gemini)$")]
    public string EmbedProvider { get; set; } = "gemini";

    public string? EmbedModel { get; set; }

    /// <summary>Đổi số chiều = tạo lại toàn bộ vector (skill db-migration).</summary>
    [Range(1, 4096)]
    public int EmbedDim { get; set; } = 768;

    public string? GeminiApiKey { get; set; }

    public string? AnthropicApiKey { get; set; }
}
