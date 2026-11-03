using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Groq chat (API tương thích OpenAI) — dự phòng khi Gemini hết lượt/lỗi. Gọi đúng một model.
/// Định dạng kiểm chứng 2026-11-03 theo docs (https://console.groq.com/docs/api-reference, /structured-outputs, /reasoning, /errors):
/// - POST {base}chat/completions, header Authorization: Bearer {key}.
/// - body { model, messages[{role: system|user|assistant, content}], temperature, max_completion_tokens, response_format,
///   reasoning_effort + include_reasoning=false (chỉ model gpt-oss) }.
/// - response_format: { type: "json_schema", json_schema: { name, strict: true, schema } } — strict bắt buộc mọi object có
///   additionalProperties=false (tự thêm ở đây); hoặc { type: "json_object" } + schema trong prompt (Ai:GroqJsonMode).
/// - response choices[0].message.content, finish_reason, usage{prompt_tokens, completion_tokens}, model.
/// - Lỗi: 429 kèm header retry-after (giây); 400 code "json_validate_failed" / 422 = model sinh JSON hỏng (chuyển model khác được).
/// Gói miễn phí giới hạn token/phút thấp (gói Developer gpt-oss-120b: 8K TPM) → trần max_completion_tokens (Ai:GroqMaxCompletionTokens).
/// Không log key, không đưa nội dung phản hồi lỗi vào message.
/// </summary>
public sealed class GroqChatProvider(HttpClient http, IOptions<AiOptions> options) : IChatModelClient
{
    /// <summary>Thêm cho token "suy nghĩ" (gpt-oss tính vào max_completion_tokens), để câu trả lời không bị cắt.</summary>
    private const int ReasoningHeadroomTokens = 512;

    private readonly AiOptions _options = options.Value;

    public string ProviderName => "groq";

    public async Task<ChatResult> CallAsync(string model, ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reasoning = string.IsNullOrWhiteSpace(_options.GroqReasoningEffort) ? null : _options.GroqReasoningEffort;
        var system = request.System;
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = request.Temperature,
            ["max_completion_tokens"] = Math.Min(request.MaxOutputTokens + (reasoning is null ? 0 : ReasoningHeadroomTokens), _options.GroqMaxCompletionTokens),
        };

        if (request.JsonSchema is not null)
        {
            if (_options.GroqJsonMode == "json_schema")
            {
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "response",
                        ["strict"] = true,
                        ["schema"] = Strict(JsonNode.Parse(request.JsonSchema)),
                    },
                };
            }
            else
            {
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                system += $"\n\n## ĐỊNH DẠNG TRẢ LỜI\nChỉ trả về MỘT object JSON đúng JSON Schema sau, không thêm chữ nào khác:\n{request.JsonSchema}";
            }
        }

        if (reasoning is not null)
        {
            body["reasoning_effort"] = reasoning;
            body["include_reasoning"] = false;
        }

        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var turn in request.Turns)
        {
            messages.Add(new JsonObject { ["role"] = turn.Role == ChatRole.User ? "user" : "assistant", ["content"] = turn.Text });
        }

        body["messages"] = messages;

        using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GroqApiKey?.Trim());

        var stopwatch = Stopwatch.StartNew();
        using var response = await AiHttp.SendAsync(http, message, cancellationToken);
        if ((int)response.StatusCode == 422 || await IsJsonGenerationFailureAsync(response, cancellationToken))
        {
            throw new AiInvalidOutputException("json_validate_failed");
        }

        AiHttp.ThrowIfFailed(response);

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;
        var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0]
            : default;
        var finishReason = choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("finish_reason", out var reason) ? reason.GetString() : null;
        var text = choice.ValueKind == JsonValueKind.Object
                   && choice.TryGetProperty("message", out var reply)
                   && reply.TryGetProperty("content", out var content)
                   && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AiInvalidOutputException(finishReason ?? "không rõ");
        }

        var (input, output) = Usage(root);
        var usedModel = root.TryGetProperty("model", out var version) ? version.GetString() ?? model : model;
        return new ChatResult(text, ProviderName, usedModel, input, output, _options.Cost(model, input, output), stopwatch.ElapsedMilliseconds);
    }

    /// <summary>Groq trả 400 + error.code "json_validate_failed" khi model sinh JSON không đúng: lỗi của model, không phải của request.</summary>
    private static async Task<bool> IsJsonGenerationFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode != 400)
        {
            return false;
        }

        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                   && code.GetString() == "json_validate_failed";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Strict mode: mọi object phải có additionalProperties = false (schema của dự án đã liệt kê đủ required).</summary>
    internal static JsonNode? Strict(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("properties") && !obj.ContainsKey("additionalProperties"))
                {
                    obj["additionalProperties"] = false;
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Strict(child);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Strict(item);
                }

                break;
        }

        return node;
    }

    private static (int Input, int Output) Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return (0, 0);
        }

        int Read(string name) => usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        return (Read("prompt_tokens"), Read("completion_tokens"));
    }
}
