using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Gemini chat qua REST (typed HttpClient + resilience: timeout, retry, circuit breaker). Gọi đúng một model; chuyển model / nhà cung cấp
/// do <see cref="FallbackChatProvider"/> lo.
/// Định dạng kiểm chứng 2026-10-04 bằng request thật tới API (docs: https://ai.google.dev/gemini-api/docs/structured-output):
/// - POST {base}models/{model}:generateContent, header x-goog-api-key.
/// - body { system_instruction, contents[{role: user|model, parts[{text}]}], generationConfig{ temperature, maxOutputTokens,
///   responseMimeType: "application/json", responseJsonSchema } }. ("response_format" bị API từ chối.)
/// - response candidates[0].content.parts[].text (bỏ part "thought"), usageMetadata{promptTokenCount, candidatesTokenCount, thoughtsTokenCount}.
/// Không log key, không đưa nội dung phản hồi lỗi vào message.
/// </summary>
public sealed class GeminiChatProvider(HttpClient http, IOptions<AiOptions> options) : IChatModelClient
{
    /// <summary>Lý do Gemini chặn nội dung: model khác cũng không nên trả lời thay → không chuyển nhà cung cấp.</summary>
    private static readonly string[] BlockedReasons = ["SAFETY", "PROHIBITED_CONTENT", "BLOCKLIST", "SPII"];

    private readonly AiOptions _options = options.Value;

    public string ProviderName => "gemini";

    public async Task<ChatResult> CallAsync(string model, ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var generationConfig = new JsonObject
        {
            ["temperature"] = request.Temperature,
            ["maxOutputTokens"] = request.MaxOutputTokens,
        };
        if (request.JsonSchema is not null)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseJsonSchema"] = JsonNode.Parse(request.JsonSchema);
        }

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) },
            ["contents"] = new JsonArray(request.Turns
                .Select(t => (JsonNode)new JsonObject
                {
                    ["role"] = t.Role == ChatRole.User ? "user" : "model",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = t.Text }),
                })
                .ToArray()),
            ["generationConfig"] = generationConfig,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"models/{model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-goog-api-key", _options.GeminiApiKey?.Trim());

        var stopwatch = Stopwatch.StartNew();
        using var response = await AiHttp.SendAsync(http, message, cancellationToken);
        AiHttp.ThrowIfFailed(response);

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var text = ExtractText(root, out var finishReason);
        if (text is null)
        {
            var blocked = root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block)
                ? block.GetString()
                : null;
            var reason = blocked ?? finishReason;
            if (blocked is not null || BlockedReasons.Contains(reason))
            {
                throw new AiProviderException($"Gemini không trả nội dung (lý do: {reason}).");
            }

            // Bị cắt (MAX_TOKENS), rỗng không rõ lý do... → model khác có thể làm được.
            throw new AiInvalidOutputException(reason ?? "không rõ");
        }

        var (input, output) = Usage(root);
        var usedModel = root.TryGetProperty("modelVersion", out var version) ? version.GetString() ?? model : model;
        return new ChatResult(text, ProviderName, usedModel, input, output, _options.Cost(model, input, output), stopwatch.ElapsedMilliseconds);
    }

    private static string? ExtractText(JsonElement root, out string? finishReason)
    {
        finishReason = null;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var candidate = candidates[0];
        finishReason = candidate.TryGetProperty("finishReason", out var reason) ? reason.GetString() : null;
        if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            var isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
            if (!isThought && part.TryGetProperty("text", out var value))
            {
                text.Append(value.GetString());
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    private static (int Input, int Output) Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage))
        {
            return (0, 0);
        }

        int Read(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        return (Read("promptTokenCount"), Read("candidatesTokenCount") + Read("thoughtsTokenCount"));
    }
}
