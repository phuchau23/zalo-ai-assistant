using System.Net;
using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Bot;
using ZaloAi.Ai.Care;
using ZaloAi.Ai.Reports;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;

namespace ZaloAi.UnitTests.Ai;

public sealed class GroqChatProviderTests
{
    private const string Schema = """
        {"type":"object","properties":{"reply":{"type":"string"},"lead":{"type":["object","null"],"properties":{"k":{"type":"string"}},"required":["k"]}},"required":["reply","lead"]}
        """;

    private static async Task<(ChatResult Result, JsonElement Body, HttpRequestMessage Request)> CallAsync(
        ChatRequest request, Action<Core.Options.AiOptions>? configure = null, string text = """{"reply":"ok","lead":null}""")
    {
        var handler = new FakeAiHandler((_, _) => AiFakes.GroqOk(text, prompt: 1_000_000, output: 1_000_000));
        var provider = AiFakes.Groq(handler, AiFakes.Options(configure));
        var result = await provider.CallAsync(AiFakes.GroqMain, request, CancellationToken.None);
        var (sent, body) = handler.Calls.ShouldHaveSingleItem();
        return (result, JsonDocument.Parse(body).RootElement, sent);
    }

    [Fact]
    public async Task Sends_openai_compatible_request_with_strict_schema()
    {
        var (_, body, request) = await CallAsync(AiFakes.Request(Schema));

        request.RequestUri!.ToString().ShouldBe("https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        request.Headers.Authorization.Parameter.ShouldBe("groq-test-key");
        body.GetProperty("model").GetString().ShouldBe(AiFakes.GroqMain);

        var messages = body.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(4);
        messages[0].GetProperty("role").GetString().ShouldBe("system");
        messages[0].GetProperty("content").GetString().ShouldBe("Bạn là trợ lý.");
        messages[1].GetProperty("role").GetString().ShouldBe("user");
        messages[2].GetProperty("role").GetString().ShouldBe("assistant");
        messages[3].GetProperty("role").GetString().ShouldBe("user");

        var format = body.GetProperty("response_format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("json_schema").GetProperty("strict").GetBoolean().ShouldBeTrue();
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        schema.GetProperty("properties").GetProperty("lead").GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        schema.GetProperty("properties").GetProperty("reply").TryGetProperty("additionalProperties", out _).ShouldBeFalse();

        body.GetProperty("reasoning_effort").GetString().ShouldBe("low");
        body.GetProperty("include_reasoning").GetBoolean().ShouldBeFalse();
        body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(2048); // 2048 + 512 suy nghĩ, trần 2048
    }

    [Fact]
    public async Task Key_pasted_with_a_trailing_new_line_still_works()
    {
        var (_, _, request) = await CallAsync(AiFakes.Request(), o => o.GroqApiKey = "groq-test-key\r\n");

        request.Headers.Authorization!.Parameter.ShouldBe("groq-test-key");
    }

    [Theory]
    [InlineData("gsk_abc\n", true)]
    [InlineData(" gsk_abc ", true)]
    [InlineData("gsk_a\nbc", false)]
    [InlineData("gsk_a bc", false)]
    public void Key_with_white_space_inside_fails_at_startup(string key, bool valid)
    {
        var options = AiFakes.Options(o => o.GroqApiKey = key);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new(options), results, validateAllProperties: true).ShouldBe(valid);
        if (!valid)
        {
            results.ShouldHaveSingleItem().MemberNames.ShouldBe(["GroqApiKey"]);
        }
    }

    [Fact]
    public async Task Small_requests_get_reasoning_headroom_under_the_cap()
    {
        var (_, body, _) = await CallAsync(AiFakes.Request() with { MaxOutputTokens = 400 });

        body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(912);
        body.TryGetProperty("response_format", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Json_object_mode_puts_schema_in_the_prompt_and_no_reasoning_when_disabled()
    {
        var (_, body, _) = await CallAsync(AiFakes.Request(Schema), o =>
        {
            o.GroqJsonMode = "json_object";
            o.GroqReasoningEffort = "";
        });

        body.GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_object");
        body.GetProperty("messages")[0].GetProperty("content").GetString()!.ShouldContain("\"required\":[\"reply\",\"lead\"]");
        body.TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
        body.TryGetProperty("include_reasoning", out _).ShouldBeFalse();
        body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(2048);
    }

    [Fact]
    public async Task Normalizes_response_like_gemini()
    {
        var (result, _, _) = await CallAsync(AiFakes.Request(Schema));

        result.Text.ShouldBe("""{"reply":"ok","lead":null}""");
        result.Provider.ShouldBe("groq");
        result.Model.ShouldBe(AiFakes.GroqMain);
        result.InputTokens.ShouldBe(1_000_000);
        result.OutputTokens.ShouldBe(1_000_000);
        result.CostUsd.ShouldBe(0.15m + 0.60m);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(AiRateLimitedException), true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, typeof(AiRequestTooLargeException), true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(AiInvalidOutputException), true)]
    [InlineData((HttpStatusCode)498, typeof(AiOverloadedException), true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(AiOverloadedException), true)]
    [InlineData(HttpStatusCode.InternalServerError, typeof(AiUnavailableException), true)]
    [InlineData(HttpStatusCode.BadRequest, typeof(AiRequestRejectedException), false)]
    [InlineData(HttpStatusCode.Unauthorized, typeof(AiRequestRejectedException), false)]
    [InlineData(HttpStatusCode.NotFound, typeof(AiRequestRejectedException), false)]
    public async Task Maps_errors(HttpStatusCode status, Type expected, bool canFallback)
    {
        var handler = new FakeAiHandler((_, _) => AiFakes.Status(status, body: """{"error":{"message":"khách 0912345678","type":"invalid_request_error"}}"""));
        var provider = AiFakes.Groq(handler, AiFakes.Options());

        var ex = await Should.ThrowAsync<AiProviderException>(() => provider.CallAsync(AiFakes.GroqMain, AiFakes.Request(), CancellationToken.None));

        ex.ShouldBeOfType(expected);
        ex.CanFallback.ShouldBe(canFallback);
        ex.Message.ShouldNotContain("0912345678");
        ex.Message.ShouldNotContain("groq-test-key");
    }

    [Fact]
    public async Task Rate_limit_reads_retry_after_header()
    {
        var handler = new FakeAiHandler((_, _) => AiFakes.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 7));
        var provider = AiFakes.Groq(handler, AiFakes.Options());

        var ex = await Should.ThrowAsync<AiRateLimitedException>(() => provider.CallAsync(AiFakes.GroqMain, AiFakes.Request(), CancellationToken.None));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Json_validate_failed_is_bad_output_not_a_request_error()
    {
        var handler = new FakeAiHandler((_, _) => AiFakes.Status(HttpStatusCode.BadRequest,
            body: """{"error":{"message":"Failed to generate JSON.","type":"invalid_request_error","code":"json_validate_failed","failed_generation":"{\"reply\":"}}"""));
        var provider = AiFakes.Groq(handler, AiFakes.Options());

        var ex = await Should.ThrowAsync<AiInvalidOutputException>(() => provider.CallAsync(AiFakes.GroqMain, AiFakes.Request(Schema), CancellationToken.None));

        ex.CanFallback.ShouldBeTrue();
        ex.Message.ShouldNotContain("reply");
    }

    [Fact]
    public async Task Empty_or_cut_off_content_is_invalid_output()
    {
        var handler = new FakeAiHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"x","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"length"}]}"""),
        });
        var provider = AiFakes.Groq(handler, AiFakes.Options());

        var ex = await Should.ThrowAsync<AiInvalidOutputException>(() => provider.CallAsync(AiFakes.GroqMain, AiFakes.Request(), CancellationToken.None));

        ex.Message.ShouldContain("length");
    }

    [Fact]
    public async Task Real_project_schemas_get_additional_properties_false_on_every_object()
    {
        foreach (var schema in new[] { PromptBuilder.ReplySchema, CareAdvisor.Schema, ReportQuestionInterpreter.Schema })
        {
            var (_, body, _) = await CallAsync(AiFakes.Request(schema));
            var sent = body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
            AssertStrict(sent);
        }

        static void AssertStrict(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (node.TryGetProperty("properties", out var properties))
            {
                node.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
                var required = node.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToHashSet();
                foreach (var property in properties.EnumerateObject())
                {
                    required.ShouldContain(property.Name, $"strict mode cần mọi trường trong required: {property.Name}");
                    AssertStrict(property.Value);
                }
            }

            if (node.TryGetProperty("items", out var items))
            {
                AssertStrict(items);
            }
        }
    }
}
