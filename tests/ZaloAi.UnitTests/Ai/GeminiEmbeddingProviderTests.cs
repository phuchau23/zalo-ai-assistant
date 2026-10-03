using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Ai;

public sealed class GeminiEmbeddingProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static AiOptions Options(int batch = 50) => new()
    {
        EmbedModel = "gemini-embedding-2",
        GeminiApiKey = "test-key",
        EmbedBatchSize = batch,
    };

    private static HttpResponseMessage Vectors(int count, int dim = 768)
    {
        var embeddings = Enumerable.Range(0, count).Select(i => new { values = Enumerable.Repeat((float)i, dim).ToArray() });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { embeddings }), Encoding.UTF8, "application/json"),
        };
    }

    private static (GeminiEmbeddingProvider Provider, FakeHandler Handler) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond, int batch = 50)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        return (new GeminiEmbeddingProvider(http, Microsoft.Extensions.Options.Options.Create(Options(batch))), handler);
    }

    [Fact]
    public async Task Sends_documented_request_format_with_key_in_header()
    {
        var (provider, handler) = Create((_, body) => Vectors(JsonDocument.Parse(body).RootElement.GetProperty("requests").GetArrayLength()));

        var vectors = await provider.EmbedDocumentsAsync([new EmbeddingDocument("Massage 60 phút", "Giá 450.000 đ"), new EmbeddingDocument(null, "Giờ mở cửa")], CancellationToken.None);

        vectors.Count.ShouldBe(2);
        var (request, body) = handler.Calls.ShouldHaveSingleItem();
        request.RequestUri!.ToString().ShouldBe("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-2:batchEmbedContents");
        request.Headers.GetValues("x-goog-api-key").ShouldBe(["test-key"]);
        request.RequestUri.Query.ShouldBeEmpty(); // key không nằm trên URL (URL hay bị ghi log)

        var requests = JsonDocument.Parse(body).RootElement.GetProperty("requests");
        requests[0].GetProperty("model").GetString().ShouldBe("models/gemini-embedding-2");
        requests[0].GetProperty("output_dimensionality").GetInt32().ShouldBe(768);
        requests[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()
            .ShouldBe("title: Massage 60 phút | text: Giá 450.000 đ");
        requests[1].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()
            .ShouldBe("title: none | text: Giờ mở cửa");
    }

    [Fact]
    public async Task Query_uses_search_task_prefix()
    {
        var (provider, handler) = Create((_, _) => Vectors(1));

        await provider.EmbedQueryAsync("giá massage", CancellationToken.None);

        var body = JsonDocument.Parse(handler.Calls.ShouldHaveSingleItem().Body).RootElement;
        body.GetProperty("requests")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()
            .ShouldBe("task: search result | query: giá massage");
    }

    [Fact]
    public async Task Large_input_is_split_into_batches()
    {
        var (provider, handler) = Create((_, body) => Vectors(JsonDocument.Parse(body).RootElement.GetProperty("requests").GetArrayLength()), batch: 2);

        var vectors = await provider.EmbedDocumentsAsync(Enumerable.Range(0, 5).Select(i => new EmbeddingDocument(null, $"đoạn {i}")).ToList(), CancellationToken.None);

        vectors.Count.ShouldBe(5);
        handler.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Rate_limit_maps_to_AiRateLimitedException()
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        await Should.ThrowAsync<AiRateLimitedException>(() => provider.EmbedQueryAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_does_not_leak_response_body()
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("nội dung khách 0912345678") });

        var ex = await Should.ThrowAsync<AiProviderException>(() => provider.EmbedQueryAsync("x", CancellationToken.None));
        ex.Message.ShouldNotContain("0912345678");
        ex.Message.ShouldContain("400");
    }

    [Theory]
    [InlineData(1, 768)] // thiếu vector
    [InlineData(2, 512)] // sai số chiều
    public async Task Unexpected_response_shape_is_rejected(int count, int dim)
    {
        var (provider, _) = Create((_, _) => Vectors(count, dim));

        await Should.ThrowAsync<AiProviderException>(() =>
            provider.EmbedDocumentsAsync([new EmbeddingDocument(null, "a"), new EmbeddingDocument(null, "b")], CancellationToken.None));
    }

    [Fact]
    public void Fake_provider_puts_similar_texts_closer()
    {
        static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

        var query = FakeEmbeddingProvider.Embed("giá massage đông y 60 phút");
        var close = FakeEmbeddingProvider.Embed("Massage Đông y 60 phút giá 450.000");
        var far = FakeEmbeddingProvider.Embed("Chi nhánh Thủ Đức Vinhomes Grand Park");

        Cosine(query, close).ShouldBeGreaterThan(Cosine(query, far));
        query.Length.ShouldBe(AiOptions.SupportedEmbedDimensions);
    }

    [Theory]
    [InlineData("gemini", null, 768, "GeminiApiKey")]
    [InlineData("gemini", "key", 1536, "EmbedDim")]
    public void Options_validation_catches_misconfiguration(string provider, string? key, int dim, string member)
    {
        var options = new AiOptions { EmbedProvider = provider, GeminiApiKey = key, EmbedDim = dim };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true).ShouldBeFalse();
        results.ShouldContain(r => r.MemberNames.Contains(member));
    }

    [Fact]
    public void Fake_provider_does_not_need_a_key()
    {
        var options = new AiOptions { EmbedProvider = "fake" };
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true).ShouldBeTrue();
    }
}
