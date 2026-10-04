using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Gemini chat qua REST (typed HttpClient + resilience: timeout, retry, circuit breaker).
/// Định dạng kiểm chứng 2026-10-04 bằng request thật tới API (docs: https://ai.google.dev/gemini-api/docs/structured-output):
/// - POST {base}models/{model}:generateContent, header x-goog-api-key.
/// - body { system_instruction, contents[{role: user|model, parts[{text}]}], generationConfig{ temperature, maxOutputTokens,
///   responseMimeType: "application/json", responseJsonSchema } }. ("response_format" bị API từ chối.)
/// - response candidates[0].content.parts[].text (bỏ part "thought"), usageMetadata{promptTokenCount, candidatesTokenCount, thoughtsTokenCount}.
/// Model chính quá tải (503) hoặc hết hạn mức (429, ví dụ gói miễn phí 20 request/ngày) sau khi đã thử lại → thử model dự phòng một lần.
/// Không log key, không đưa nội dung phản hồi lỗi vào message.
/// </summary>
public sealed class GeminiChatProvider(HttpClient http, IOptions<AiOptions> options) : IChatProvider
{
    private const string ProviderName = "gemini";
    private readonly AiOptions _options = options.Value;

    public async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await CallAsync(_options.ChatModel, request, cancellationToken);
        }
        catch (AiProviderException ex) when (ex is AiOverloadedException or AiRateLimitedException
                                             && !string.IsNullOrWhiteSpace(_options.ChatFallbackModel)
                                             && _options.ChatFallbackModel != _options.ChatModel)
        {
            return await CallAsync(_options.ChatFallbackModel, request, cancellationToken);
        }
    }

    private async Task<ChatResult> CallAsync(string model, ChatRequest request, CancellationToken cancellationToken)
    {
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
        message.Headers.Add("x-goog-api-key", _options.GeminiApiKey);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiTimeoutException(ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            throw new AiTimeoutException(ex);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            throw new AiOverloadedException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException("Không kết nối được dịch vụ AI (Gemini).", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.TooManyRequests:
                    throw new AiRateLimitedException();
                case HttpStatusCode.ServiceUnavailable:
                    throw new AiOverloadedException();
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AiProviderException($"Gemini trả lỗi HTTP {(int)response.StatusCode}.");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            var text = ExtractText(root, out var finishReason);
            if (text is null)
            {
                throw new AiProviderException($"Gemini không trả nội dung (lý do: {finishReason ?? "không rõ"}).");
            }

            var (input, output) = Usage(root);
            var usedModel = root.TryGetProperty("modelVersion", out var version) ? version.GetString() ?? model : model;
            return new ChatResult(text, ProviderName, usedModel, input, output, _options.Cost(model, input, output), stopwatch.ElapsedMilliseconds);
        }
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

