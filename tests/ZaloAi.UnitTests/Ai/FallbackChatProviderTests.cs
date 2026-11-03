using System.Net;
using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Errors;
using static ZaloAi.UnitTests.Ai.AiFakes;

namespace ZaloAi.UnitTests.Ai;

/// <summary>Chuỗi dự phòng Gemini → Groq: chạy qua HTTP giả của từng nhà cung cấp (không gọi mạng thật).</summary>
public sealed class FallbackChatProviderTests
{
    private const string Schema = """
        {"type":"object","properties":{"reply":{"type":"string"},"confidence":{"type":"string","enum":["high","low"]}},"required":["reply","confidence"]}
        """;

    private const string Answer = """{"reply":"Dạ massage 60 phút 450.000đ ạ","confidence":"high"}""";

    private static readonly ChatRequest JsonRequest = Request(Schema);

    // 1
    [Fact]
    public async Task Gemini_success_does_not_call_groq()
    {
        var (chat, handler, _) = Chain((model, _) => model == GeminiMain ? GeminiOk(Answer) : throw new InvalidOperationException("không được gọi"));

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain]);
        result.Text.ShouldBe(Answer);
        result.Provider.ShouldBe("gemini");
    }

    // 2
    [Fact]
    public async Task Gemini_rate_limited_then_groq_answers_with_the_same_normalized_shape()
    {
        var (geminiChat, _, _) = Chain((_, _) => GeminiOk(Answer, prompt: 100, output: 20));
        var fromGemini = await geminiChat.ChatAsync(JsonRequest, CancellationToken.None);

        var (chat, handler, _) = Chain((model, _) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? Status(HttpStatusCode.TooManyRequests)
            : GroqOk(Answer, model, prompt: 100, output: 20));
        var fromGroq = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain]);
        fromGroq.Text.ShouldBe(fromGemini.Text);
        fromGroq.InputTokens.ShouldBe(fromGemini.InputTokens);
        fromGroq.OutputTokens.ShouldBe(fromGemini.OutputTokens);
        fromGroq.Provider.ShouldBe("groq");
        fromGroq.Model.ShouldBe(GroqMain);
        fromGroq.CostUsd.ShouldBeGreaterThan(0m);
    }

    // 3
    [Fact]
    public async Task Gemini_and_groq_main_rate_limited_then_groq_backup_answers()
    {
        var (chat, handler, _) = Chain((model, _) => model == GroqBackup
            ? GroqOk(Answer, model)
            : Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: model == GroqMain ? 45 : null));

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain, GroqBackup]); // retry-after 45s > 10s → không chờ, chuyển model
        result.Model.ShouldBe(GroqBackup);
        result.Text.ShouldBe(Answer);
    }

    // 4
    [Fact]
    public async Task All_providers_failing_throws_a_friendly_provider_error()
    {
        var (chat, handler, _) = Chain((model, _) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? Status(HttpStatusCode.TooManyRequests)
            : Status(HttpStatusCode.ServiceUnavailable));

        var ex = await Should.ThrowAsync<AiProviderException>(() => chat.ChatAsync(JsonRequest, CancellationToken.None));

        ex.Message.ShouldBe("Tất cả dịch vụ AI đều đang lỗi hoặc hết lượt, vui lòng thử lại sau.");
        ex.InnerException.ShouldBeOfType<AiOverloadedException>();
        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain, GroqBackup]);
    }

    // 5
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Gemini_request_or_key_error_does_not_fall_back(HttpStatusCode status)
    {
        var (chat, handler, _) = Chain((model, _) => model == GeminiMain ? Status(status) : GroqOk(Answer, model));

        var ex = await Should.ThrowAsync<AiRequestRejectedException>(() => chat.ChatAsync(JsonRequest, CancellationToken.None));

        ex.StatusCode.ShouldBe((int)status);
        handler.Models().ShouldBe([GeminiMain]);
    }

    // 6
    [Fact]
    public async Task Groq_json_fence_and_extra_text_are_cleaned()
    {
        var (chat, handler, _) = Chain((model, _) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? Status(HttpStatusCode.TooManyRequests)
            : GroqOk($"Đây là kết quả:\n```json\n{Answer}\n```\nCảm ơn", model));

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        result.Text.ShouldBe(Answer);
        handler.Models().Count(m => m == GroqMain).ShouldBe(1);
    }

    [Fact]
    public async Task Wrong_shape_is_retried_once_with_a_format_reminder_then_moves_on()
    {
        var groqMainCalls = 0;
        var (chat, handler, _) = Chain((model, _) => model switch
        {
            GroqMain => GroqOk(++groqMainCalls == 1 ? """{"reply":"thiếu confidence"}""" : """{"reply":"vẫn sai","confidence":"maybe"}""", model, prompt: 50, output: 5),
            GroqBackup => GroqOk(Answer, model, prompt: 100, output: 20),
            _ => Status(HttpStatusCode.TooManyRequests),
        });

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain, GroqMain, GroqBackup]);
        var retry = JsonDocument.Parse(handler.Calls[3].Body).RootElement;
        retry.GetProperty("messages")[0].GetProperty("content").GetString()!.ShouldContain("Chỉ trả về đúng MỘT object JSON");
        retry.GetProperty("temperature").GetDouble().ShouldBe(0);
        result.Text.ShouldBe(Answer);
        result.InputTokens.ShouldBe(200); // 2 lượt hỏng (50 + 50) + lượt đúng (100): không mất chi phí
        result.OutputTokens.ShouldBe(30);
    }

    [Fact]
    public async Task Format_reminder_fixes_the_answer_on_the_same_model()
    {
        var calls = 0;
        var (chat, handler, _) = Chain((model, _) => model == GeminiMain
            ? GeminiOk(++calls == 1 ? "không phải JSON" : Answer)
            : throw new InvalidOperationException("không được gọi"));

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain, GeminiMain]);
        result.Text.ShouldBe(Answer);
    }

    // 7
    [Fact]
    public async Task Gemini_timeout_falls_back()
    {
        var (chat, handler, _) = Chain((model, _) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? throw new TaskCanceledException()
            : GroqOk(Answer, model));

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain]);
        result.Provider.ShouldBe("groq");
    }

    [Fact]
    public async Task Server_and_network_errors_fall_back()
    {
        var (chat, handler, _) = Chain((model, _) => model switch
        {
            GeminiMain => Status(HttpStatusCode.InternalServerError),
            GeminiLite => throw new HttpRequestException("không kết nối được"),
            _ => GroqOk(Answer, model),
        });

        (await chat.ChatAsync(JsonRequest, CancellationToken.None)).Model.ShouldBe(GroqMain);
        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain]);
    }

    [Fact]
    public async Task Gemini_safety_block_does_not_fall_back()
    {
        var (chat, handler, _) = Chain((model, _) => model == GeminiMain
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"candidates":[{"finishReason":"SAFETY"}]}""") }
            : GroqOk(Answer, model));

        var ex = await Should.ThrowAsync<AiProviderException>(() => chat.ChatAsync(JsonRequest, CancellationToken.None));

        ex.Message.ShouldContain("SAFETY");
        handler.Models().ShouldBe([GeminiMain]);
    }

    [Fact]
    public async Task Short_retry_after_waits_and_retries_the_same_model()
    {
        var calls = 0;
        var (chat, handler, _) = Chain((model, _) => model switch
        {
            GroqMain => ++calls == 1 ? Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 0) : GroqOk(Answer, model),
            GroqBackup => throw new InvalidOperationException("không được gọi"),
            _ => Status(HttpStatusCode.TooManyRequests),
        });

        var result = await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GeminiMain, GeminiLite, GroqMain, GroqMain]);
        result.Model.ShouldBe(GroqMain);
    }

    [Fact]
    public async Task Rate_limited_models_are_skipped_on_the_next_request()
    {
        var (chat, handler, cooldowns) = Chain((model, _) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? Status(HttpStatusCode.TooManyRequests)
            : GroqOk(Answer, model));

        await chat.ChatAsync(JsonRequest, CancellationToken.None);
        handler.Calls.Clear();
        await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Models().ShouldBe([GroqMain]);
        cooldowns.IsCooling($"gemini/{GeminiMain}", DateTimeOffset.UtcNow.AddSeconds(59)).ShouldBeTrue();
        cooldowns.IsCooling($"gemini/{GeminiMain}", DateTimeOffset.UtcNow.AddSeconds(61)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_every_model_is_cooling_down_they_are_all_tried_anyway()
    {
        var cooldowns = new ChatCooldowns();
        foreach (var model in new[] { $"gemini/{GeminiMain}", $"gemini/{GeminiLite}", $"groq/{GroqMain}", $"groq/{GroqBackup}" })
        {
            cooldowns.Cool(model, DateTimeOffset.UtcNow.AddMinutes(5));
        }

        var (chat, handler, _) = Chain((model, _) => GeminiOk(Answer), cooldowns: cooldowns);

        (await chat.ChatAsync(JsonRequest, CancellationToken.None)).Provider.ShouldBe("gemini");
        handler.Models().ShouldBe([GeminiMain]);
    }

    [Fact]
    public async Task Groq_request_too_large_moves_to_the_next_model_without_cutting_the_prompt()
    {
        var (chat, handler, _) = Chain((model, _) => model switch
        {
            GroqMain => Status(HttpStatusCode.RequestEntityTooLarge),
            GroqBackup => GroqOk(Answer, model),
            _ => Status(HttpStatusCode.TooManyRequests),
        });

        await chat.ChatAsync(JsonRequest, CancellationToken.None);

        handler.Calls[2].Body.ShouldBe(handler.Calls[3].Body.Replace(GroqBackup, GroqMain, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_text_requests_skip_json_checks()
    {
        var (chat, _, _) = Chain((model, _) => model.StartsWith("gemini", StringComparison.Ordinal)
            ? Status(HttpStatusCode.TooManyRequests)
            : GroqOk("- Khách hỏi giá massage\n- Chưa đặt lịch", model));

        var result = await chat.ChatAsync(Request(), CancellationToken.None);

        result.Text.ShouldBe("- Khách hỏi giá massage\n- Chưa đặt lịch");
    }
}
