using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Ai;

public sealed class GeminiChatProviderTests
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

    private static AiOptions Options() => new()
    {
        ChatModel = "gemini-3.8-flash",
        ChatFallbackModel = "gemini-3.5-flash-lite",
        GeminiApiKey = "test-key",
        Pricing = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-3.8-flash"] = new ModelPrice { InputPerMillionUsd = 0.75m, OutputPerMillionUsd = 3.75m },
            ["gemini-3.5-flash-lite"] = new ModelPrice { InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 2.50m },
        },
    };

    private static HttpResponseMessage Ok(string text, int prompt = 1000, int candidates = 100, int thoughts = 50)
    {
        var body = new
        {
            candidates = new[]
            {
                new
                {
                    content = new { role = "model", parts = new object[] { new { text = "đang nghĩ...", thought = true }, new { text } } },
                    finishReason = "STOP",
                },
            },
            usageMetadata = new { promptTokenCount = prompt, candidatesTokenCount = candidates, thoughtsTokenCount = thoughts },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    private static (GeminiChatProvider Provider, FakeHandler Handler) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/") };
        return (new GeminiChatProvider(http, Microsoft.Extensions.Options.Options.Create(Options())), handler);
    }

    private static readonly ChatRequest Request = new(
        "Bạn là trợ lý.",
        [new ChatTurn(ChatRole.User, "Giá massage?"), new ChatTurn(ChatRole.Assistant, "Dạ 450k ạ"), new ChatTurn(ChatRole.User, "Cảm ơn")],
        """{"type":"object","properties":{"reply":{"type":"string"}},"required":["reply"]}""");

    [Fact]
    public async Task Sends_verified_request_format()
    {
        var (provider, handler) = Create((_, _) => Ok("""{"reply":"ok"}"""));

        await provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None);

        var (request, body) = handler.Calls.ShouldHaveSingleItem();
        request.RequestUri!.ToString().ShouldBe("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent");
        request.Headers.GetValues("x-goog-api-key").ShouldBe(["test-key"]);
        request.RequestUri.Query.ShouldBeEmpty();

        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString().ShouldBe("Bạn là trợ lý.");
        var contents = root.GetProperty("contents");
        contents.GetArrayLength().ShouldBe(3);
        contents[0].GetProperty("role").GetString().ShouldBe("user");
        contents[1].GetProperty("role").GetString().ShouldBe("model");
        var config = root.GetProperty("generationConfig");
        config.GetProperty("responseMimeType").GetString().ShouldBe("application/json");
        config.GetProperty("responseJsonSchema").GetProperty("required")[0].GetString().ShouldBe("reply");
        config.GetProperty("maxOutputTokens").GetInt32().ShouldBe(2048);
        root.TryGetProperty("response_format", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Skips_thought_parts_and_counts_thinking_tokens_as_output()
    {
        var (provider, _) = Create((_, _) => Ok("""{"reply":"ok"}""", prompt: 1_000_000, candidates: 100_000, thoughts: 100_000));

        var result = await provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None);

        result.Text.ShouldBe("""{"reply":"ok"}""");
        result.InputTokens.ShouldBe(1_000_000);
        result.OutputTokens.ShouldBe(200_000);
        result.CostUsd.ShouldBe(0.75m + 0.75m); // 1M input × 0.75 + 0.2M output × 3.75
        result.Provider.ShouldBe("gemini");
    }

    [Fact]
    public async Task Uses_the_requested_model_and_its_price()
    {
        var (provider, handler) = Create((_, _) => Ok("""{"reply":"ok"}""", prompt: 1_000_000, candidates: 0, thoughts: 0));

        var result = await provider.CallAsync("gemini-3.5-flash-lite", Request, CancellationToken.None);

        handler.Calls.ShouldHaveSingleItem().Request.RequestUri!.ToString().ShouldContain("gemini-3.5-flash-lite");
        result.CostUsd.ShouldBe(0.30m);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(AiRateLimitedException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(AiOverloadedException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(AiUnavailableException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(AiUnavailableException))]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, typeof(AiRequestTooLargeException))]
    public async Task Temporary_errors_can_fall_back(HttpStatusCode status, Type expected)
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(status));

        var ex = await Should.ThrowAsync<AiProviderException>(() => provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None));

        ex.ShouldBeOfType(expected);
        ex.CanFallback.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Request_and_key_errors_do_not_fall_back(HttpStatusCode status)
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(status));

        var ex = await Should.ThrowAsync<AiRequestRejectedException>(() => provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None));

        ex.CanFallback.ShouldBeFalse();
        ex.StatusCode.ShouldBe((int)status);
    }

    [Fact]
    public async Task Timeout_and_network_errors_can_fall_back()
    {
        var (timeout, _) = Create((_, _) => throw new TaskCanceledException());
        var (network, _) = Create((_, _) => throw new HttpRequestException("dns"));

        (await Should.ThrowAsync<AiTimeoutException>(() => timeout.CallAsync("gemini-3.8-flash", Request, CancellationToken.None))).CanFallback.ShouldBeTrue();
        (await Should.ThrowAsync<AiUnavailableException>(() => network.CallAsync("gemini-3.8-flash", Request, CancellationToken.None))).CanFallback.ShouldBeTrue();
    }

    [Fact]
    public async Task Error_message_does_not_leak_response_body_or_key()
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bí mật khách hàng 0912345678"}}"""),
        });

        var ex = await Should.ThrowAsync<AiProviderException>(() => provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None));
        ex.Message.ShouldNotContain("0912345678");
        ex.Message.ShouldNotContain("test-key");
        ex.Message.ShouldContain("400");
    }

    [Fact]
    public async Task Safety_block_is_provider_error_without_fallback()
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"candidates":[{"finishReason":"SAFETY"}]}"""),
        });

        var ex = await Should.ThrowAsync<AiProviderException>(() => provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None));
        ex.Message.ShouldContain("SAFETY");
        ex.CanFallback.ShouldBeFalse();
    }

    [Fact]
    public async Task Cut_off_answer_is_invalid_output_that_can_fall_back()
    {
        var (provider, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[]}}]}"""),
        });

        var ex = await Should.ThrowAsync<AiInvalidOutputException>(() => provider.CallAsync("gemini-3.8-flash", Request, CancellationToken.None));
        ex.Message.ShouldContain("MAX_TOKENS");
        ex.CanFallback.ShouldBeTrue();
    }

    [Fact]
    public async Task Fake_provider_answers_from_first_chunk_or_hands_off()
    {
        var fake = new FakeChatProvider();
        var withChunk = await fake.ChatAsync(Request with { System = "Dữ liệu:\n[C1] (mã MS60) Massage 60 phút\nGiá 450.000 đ\n" }, CancellationToken.None);
        var json = JsonDocument.Parse(withChunk.Text).RootElement;
        json.GetProperty("confidence").GetString().ShouldBe("high");
        json.GetProperty("messages")[0].GetString()!.ShouldContain("450.000");
        json.GetProperty("messages").GetArrayLength().ShouldBe(2);
        json.GetProperty("used_chunk_ids")[0].GetString().ShouldBe("C1");

        var noChunk = JsonDocument.Parse((await fake.ChatAsync(Request, CancellationToken.None)).Text).RootElement;
        noChunk.GetProperty("needs_human").GetBoolean().ShouldBeTrue();
    }
}
