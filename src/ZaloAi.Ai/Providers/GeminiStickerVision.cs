using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Gemini xem hình sticker → một câu ngắn "hình gì – ý nghĩa" (chủ dự án chốt 2026-11-25). Gemini đọc được ảnh; Groq gpt-oss thì không.
/// Kiểm chứng 2026-11-25 (API reference generateContent, https://aistudio.google.com/api-reference/generate-content): phần ảnh trong
/// contents[].parts là { "inlineData": { "mimeType", "data" (base64) } }, ghép chung mảng parts với phần chữ được; mimeType ảnh gồm
/// image/png, image/jpeg, image/webp, image/gif...; ảnh gửi kèm giới hạn tổng request 20MB. Docs không nói rõ ép JSON khi có ảnh → xin trả
/// một dòng chữ thường rồi tự làm sạch. Không log key, không ghi nội dung phản hồi vào log.
/// </summary>
public sealed class GeminiStickerVision(HttpClient http, IOptions<AiOptions> options) : IStickerVision
{
    /// <summary>Câu tả tối đa chừng này ký tự (lưu vào nội dung tin, đưa cho bot).</summary>
    public const int MaxMeaningLength = 80;

    public const string Instruction =
        "Bạn xem MỘT sticker (hình dán) khách gửi trong Zalo cho cửa hàng. Trả lời đúng MỘT dòng tiếng Việt, tối đa 12 chữ, dạng " +
        "\"hình gì – ý nghĩa thường gặp\". Ví dụ: \"giơ ngón cái – đồng ý, ok\"; \"mèo cười lăn – vui, buồn cười\"; \"vẫy tay – chào\"; " +
        "\"cúi đầu – cảm ơn\"; \"khóc – buồn\". Có chữ trên hình thì dựa vào chữ đó. Không thêm lời nào khác.";

    private readonly AiOptions _options = options.Value;

    public async Task<StickerVisionResult?> DescribeAsync(byte[] image, string mimeType, string? keyword, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var model = string.IsNullOrWhiteSpace(_options.StickerVisionModel) ? _options.ChatModel : _options.StickerVisionModel.Trim();
        var hint = string.IsNullOrWhiteSpace(keyword) ? "(trống)" : keyword.Trim();
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = Instruction }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = $"Chữ khóa Zalo gắn với sticker này (có thể trống hoặc không chính xác): {hint}" },
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = Convert.ToBase64String(image) } }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 512 },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"models/{model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-goog-api-key", _options.GeminiApiKey?.Trim());

        using var response = await AiHttp.SendAsync(http, message, cancellationToken);
        AiHttp.ThrowIfFailed(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var (input, output, cached) = GeminiChatProvider.Usage(root);
        var usedModel = root.TryGetProperty("modelVersion", out var version) ? version.GetString() ?? model : model;
        var meaning = Clean(GeminiChatProvider.ExtractText(root, out _));
        return meaning is null
            ? null
            : new StickerVisionResult(meaning, new BotUsage(UsageKind.Chat, "gemini", usedModel, input, output, _options.Cost(model, input, output, cached), cached));
    }

    /// <summary>Dòng đầu, bỏ ngoặc kép / dấu chấm cuối, cắt ≤ <see cref="MaxMeaningLength"/> ký tự. Rỗng → null.</summary>
    public static string? Clean(string? text)
    {
        var line = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        line = line?.Trim('"', '“', '”', '\'', '*', ' ', '.');
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return line.Length <= MaxMeaningLength ? line : line[..MaxMeaningLength].TrimEnd();
    }
}

/// <summary>AI giả cho test / chạy không khóa: có chữ khóa thì dùng, không thì coi như sticker giơ ngón cái. Không tốn tiền.</summary>
public sealed class FakeStickerVision : IStickerVision
{
    public const string DefaultMeaning = "giơ ngón cái – đồng ý, ok";

    public Task<StickerVisionResult?> DescribeAsync(byte[] image, string mimeType, string? keyword, CancellationToken cancellationToken) =>
        Task.FromResult<StickerVisionResult?>(new StickerVisionResult(
            string.IsNullOrWhiteSpace(keyword) ? DefaultMeaning : $"sticker “{keyword.Trim()}”",
            new BotUsage(UsageKind.Chat, "fake", "fake-vision", 0, 0, 0m)));
}
