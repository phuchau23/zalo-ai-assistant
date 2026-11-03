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
/// Cấu hình AI. Chat: Gemini, Groq làm dự phòng khi Gemini hết lượt/lỗi (chủ dự án chốt 2026-11-03). Embedding: chỉ Gemini.
/// Production phải dùng gói Gemini TRẢ PHÍ: gói miễn phí cho phép Google dùng dữ liệu để cải thiện sản phẩm.
/// </summary>
public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "Ai";

    /// <summary>Số chiều cột chunks.embedding trong database. Đổi = migration + đánh chỉ mục lại (skill db-migration).</summary>
    public const int SupportedEmbedDimensions = 768;

    /// <summary>
    /// gemini: gọi AI thật theo chuỗi <see cref="ChatProviders"/> (tên giữ "gemini" cho cấu hình cũ).
    /// fake: trả lời giả tính tại chỗ (test, chạy không mạng) — không dùng ở production.
    /// </summary>
    [Required, RegularExpression("^(gemini|fake)$")]
    public string ChatProvider { get; set; } = "gemini";

    /// <summary>
    /// Thứ tự nhà cung cấp chat, cách nhau dấu phẩy. gemini = <see cref="ChatModel"/> rồi <see cref="ChatFallbackModel"/>;
    /// groq = <see cref="GroqModels"/> theo thứ tự (chỉ dùng khi có <see cref="GroqApiKey"/>). Lỗi tạm thời / hết lượt / JSON hỏng
    /// → thử bước tiếp theo; request sai, key sai → dừng (AiRequestRejectedException).
    /// </summary>
    [Required, RegularExpression(@"^\s*(gemini|groq)\s*(,\s*(gemini|groq)\s*)*$")]
    public string ChatProviders { get; set; } = "gemini,groq";

    /// <summary>Kiểm chứng 2026-10-04: https://ai.google.dev/gemini-api/docs/models</summary>
    [Required]
    public string ChatModel { get; set; } = "gemini-3.8-flash";

    /// <summary>Model Gemini dự phòng khi model chính lỗi tạm thời hoặc hết hạn mức. Để trống = không dùng.</summary>
    public string? ChatFallbackModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>Model hết lượt (429) hoặc quá tải (503) → bỏ qua trong bấy nhiêu giây (hoặc theo retry-after nếu lâu hơn). 0 = không bỏ qua.</summary>
    [Range(0, 3600)]
    public int ChatCooldownSeconds { get; set; } = 60;

    /// <summary>429 kèm retry-after ≤ số giây này → chờ rồi thử lại cùng model một lần; lâu hơn → chuyển bước tiếp theo.</summary>
    [Range(0, 60)]
    public int ChatMaxRetryAfterSeconds { get; set; } = 10;

    /// <summary>Key Groq (console.groq.com). Trống = không dùng Groq. Chỉ đặt qua user-secrets "Ai:GroqApiKey" hoặc env Ai__GroqApiKey.</summary>
    public string? GroqApiKey { get; set; }

    [Url]
    public string GroqBaseUrl { get; set; } = "https://api.groq.com/openai/v1/";

    /// <summary>Model Groq theo thứ tự thử, cách nhau dấu phẩy. Kiểm chứng 2026-11-03: https://console.groq.com/docs/models (model production).</summary>
    public string GroqModels { get; set; } = "openai/gpt-oss-120b,openai/gpt-oss-20b";

    /// <summary>
    /// json_schema: Structured Outputs strict (chỉ một số model, ví dụ gpt-oss). json_object: JSON thường, schema gửi trong prompt
    /// (mọi model) — dùng khi model không hỗ trợ json_schema hoặc Groq từ chối schema. Kiểm chứng 2026-11-03:
    /// https://console.groq.com/docs/structured-outputs
    /// </summary>
    [Required, RegularExpression("^(json_schema|json_object)$")]
    public string GroqJsonMode { get; set; } = "json_schema";

    /// <summary>low | medium | high (model gpt-oss). Trống = không gửi (model không có chế độ suy nghĩ sẽ báo lỗi 400 nếu gửi).</summary>
    [RegularExpression("^(low|medium|high)?$")]
    public string? GroqReasoningEffort { get; set; } = "low";

    /// <summary>Trần max_completion_tokens gửi Groq (gồm token suy nghĩ). Thấp để prompt + trần không vượt số token/phút của gói.</summary>
    [Range(256, 65536)]
    public int GroqMaxCompletionTokens { get; set; } = 2048;

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

    /// <summary>Khoảng cách (ms) giữa các tin khi bot trả lời bằng nhiều tin ngắn, cho giống người đang gõ. 0 = gửi liền.</summary>
    [Range(0, 5000)]
    public int BotMessageGapMs { get; set; } = 1200;

    /// <summary>Bảng giá theo tên model (appsettings.json, section Ai:Pricing). Model không có giá → chi phí ghi 0.</summary>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal Cost(string model, int inputTokens, int outputTokens) =>
        Pricing.TryGetValue(model, out var price)
            ? ((inputTokens * price.InputPerMillionUsd) + (outputTokens * price.OutputPerMillionUsd)) / 1_000_000m
            : 0m;

    public IReadOnlyList<string> ChatProviderOrder => SplitList(ChatProviders);

    public IReadOnlyList<string> GroqModelList => SplitList(GroqModels);

    public bool GroqEnabled =>
        !string.IsNullOrWhiteSpace(GroqApiKey) && ChatProviderOrder.Contains("groq") && GroqModelList.Count > 0;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EmbedDim != SupportedEmbedDimensions)
        {
            yield return new ValidationResult(
                $"{nameof(EmbedDim)} phải bằng {SupportedEmbedDimensions} (số chiều cột vector trong database).",
                [nameof(EmbedDim)]);
        }

        var chatUsesGemini = ChatProvider == "gemini" && ChatProviderOrder.Contains("gemini");
        if ((EmbedProvider == "gemini" || chatUsesGemini) && string.IsNullOrWhiteSpace(GeminiApiKey))
        {
            yield return new ValidationResult(
                $"{nameof(GeminiApiKey)} bắt buộc khi dùng Gemini (đặt bằng user-secrets \"Ai:GeminiApiKey\" hoặc env Ai__GeminiApiKey).",
                [nameof(GeminiApiKey)]);
        }

        // Dán key hay dính dấu xuống dòng: ở hai đầu thì lúc gọi tự cắt; ở giữa key là key hỏng → báo ngay khi khởi động.
        foreach (var (name, key) in new[] { (nameof(GeminiApiKey), GeminiApiKey), (nameof(GroqApiKey), GroqApiKey) })
        {
            if (key is not null && key.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                yield return new ValidationResult(
                    $"{name} có khoảng trắng hoặc dấu xuống dòng ở giữa key — đặt lại key trên một dòng.",
                    [name]);
            }
        }

        if (ChatProvider == "gemini" && !chatUsesGemini && !GroqEnabled)
        {
            yield return new ValidationResult(
                $"{nameof(ChatProviders)} không còn nhà cung cấp chat nào dùng được (groq cần {nameof(GroqApiKey)} và {nameof(GroqModels)}).",
                [nameof(ChatProviders), nameof(GroqApiKey)]);
        }
    }

    private static List<string> SplitList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
}
