using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Gemini embedding qua REST (typed HttpClient + resilience handler: timeout, retry, circuit breaker).
/// Định dạng kiểm chứng 2026-10-03 từ https://ai.google.dev/gemini-api/docs/embeddings:
/// - POST {base}models/{model}:batchEmbedContents, header x-goog-api-key; body { requests: [ { model, content, output_dimensionality } ] }
/// - gemini-embedding-2 không có tham số task_type: ghi nhiệm vụ vào nội dung —
///   tài liệu "title: {tiêu đề} | text: {nội dung}", câu hỏi "task: search result | query: {câu hỏi}".
/// - Response { embeddings: [ { values: [...] } ] }.
/// Không log key (nằm ở header) và không log nội dung gửi đi.
/// </summary>
public sealed class GeminiEmbeddingProvider(HttpClient http, IOptions<AiOptions> options) : IEmbeddingProvider
{
    private readonly AiOptions _options = options.Value;

    public string ModelName => _options.EmbedModel;

    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<EmbeddingDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var results = new List<float[]>(documents.Count);
        foreach (var batch in documents.Chunk(_options.EmbedBatchSize))
        {
            var texts = batch.Select(d => $"title: {(string.IsNullOrWhiteSpace(d.Title) ? "none" : d.Title)} | text: {d.Text}");
            results.AddRange(await EmbedAsync(texts.ToList(), cancellationToken));
        }

        return results;
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var vectors = await EmbedAsync([$"task: search result | query: {query}"], cancellationToken);
        return vectors[0];
    }

    private async Task<List<float[]>> EmbedAsync(List<string> texts, CancellationToken cancellationToken)
    {
        var model = $"models/{_options.EmbedModel}";
        var body = new BatchRequest(texts
            .Select(t => new EmbedRequest(model, new Content([new Part(t)]), _options.EmbedDim))
            .ToList());

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{model}:batchEmbedContents")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-goog-api-key", _options.GeminiApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiTimeoutException(ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            throw new AiTimeoutException(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException("Không kết nối được dịch vụ tạo vector (Gemini).", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AiRateLimitedException();
            }

            if (!response.IsSuccessStatusCode)
            {
                // Không đưa body lỗi vào message: có thể lặp lại nội dung đã gửi.
                throw new AiProviderException($"Gemini trả lỗi HTTP {(int)response.StatusCode} khi tạo vector.");
            }

            var payload = await response.Content.ReadFromJsonAsync<BatchResponse>(cancellationToken)
                ?? throw new AiProviderException("Gemini trả dữ liệu rỗng.");
            if (payload.Embeddings is not { } embeddings || embeddings.Count != texts.Count)
            {
                throw new AiProviderException("Gemini trả số vector không khớp số đoạn gửi đi.");
            }

            var vectors = embeddings.Select(e => e.Values ?? []).ToList();
            if (vectors.Any(v => v.Length != _options.EmbedDim))
            {
                throw new AiProviderException($"Gemini trả vector sai số chiều (cần {_options.EmbedDim}).");
            }

            return vectors;
        }
    }

    private sealed record BatchRequest([property: JsonPropertyName("requests")] IReadOnlyList<EmbedRequest> Requests);

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("content")] Content Content,
        [property: JsonPropertyName("output_dimensionality")] int OutputDimensionality);

    private sealed record Content([property: JsonPropertyName("parts")] IReadOnlyList<Part> Parts);

    private sealed record Part([property: JsonPropertyName("text")] string Text);

    private sealed record BatchResponse([property: JsonPropertyName("embeddings")] List<EmbeddingValues>? Embeddings);

    private sealed record EmbeddingValues([property: JsonPropertyName("values")] float[]? Values);
}
