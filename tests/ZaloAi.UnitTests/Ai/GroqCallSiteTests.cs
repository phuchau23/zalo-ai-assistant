using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ZaloAi.Ai.Bot;
using ZaloAi.Ai.Care;
using ZaloAi.Ai.Reports;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.UnitTests.Customers;

namespace ZaloAi.UnitTests.Ai;

/// <summary>
/// Mọi chỗ gọi AI chạy hết luồng xử lý phía sau với câu trả lời từ Groq (Gemini hết lượt): không đọc sai trường, không vỡ logic,
/// dữ liệu cá nhân vẫn được che trước khi gửi Groq và ghép lại sau.
/// </summary>
public sealed class GroqCallSiteTests
{
    private const string Phone = "0912345678";

    private static readonly BotProfile Spa = new("Khoa Học Huyệt Đạo", "spa", "Huyệt Đạo", "em", BotTone.Friendly, null, "https://example.vn/bao-mat");

    private static readonly KnowledgeHit Massage = new(Guid.NewGuid(), "item", Guid.NewGuid(), "DV-MS60", "Massage body 60 phút", "Massage body 60 phút: 450.000đ.", 0.8, false);

    /// <summary>Nội dung đã gửi Groq (system + các tin), đọc từ body JSON (chữ Việt trong body bị mã hóa \uXXXX).</summary>
    private static string GroqPrompt(FakeAiHandler handler) => string.Join("\n", handler.Calls
        .Where(c => c.Request.RequestUri!.ToString().EndsWith("chat/completions", StringComparison.Ordinal))
        .SelectMany(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("messages").EnumerateArray())
        .Select(m => m.GetProperty("content").GetString()));

    [Fact]
    public async Task Bot_reply_from_groq_goes_through_the_same_checks_and_restores_pii()
    {
        const string reply = """
            {"messages":["Dạ massage body 60 phút giá 450.000đ ạ.","Em ghi số [PHONE_1] để nhân viên gọi lại chị nha."],
             "customer_salutation":"chị","used_chunk_ids":["C1"],"confidence":"high","needs_human":false,"handoff_reason":null,
             "urgency":"none","health_topic":false,"sentiment":"neutral","lead_fields":[{"key":"phone","value":"[PHONE_1]"}],"booking":null}
            """;
        var (chat, handler) = AiFakes.GeminiOutGroqAnswers(_ => $"```json\n{reply}\n```");
        var engine = new BotEngine(chat, new StaticKnowledge(Massage), Microsoft.Extensions.Options.Options.Create(new AiOptions()), TimeProvider.System, NullLogger<BotEngine>.Instance);

        var result = await engine.ReplyAsync(
            new BotTurnRequest(Guid.NewGuid(), Spa, $"chị hỏi giá massage 60 phút, số chị {Phone}", [], null, false, null),
            CancellationToken.None);

        result.NeedsHuman.ShouldBeFalse();
        result.Messages.ShouldBe(["Dạ massage body 60 phút giá 450.000đ ạ.", $"Em ghi số {Phone} để nhân viên gọi lại chị nha."]);
        result.DetectedSalutation.ShouldBe("chị");
        result.Confidence.ShouldBe("high");
        result.Usage.ShouldHaveSingleItem().Provider.ShouldBe("groq");
        JsonDocument.Parse(result.TraceJson).RootElement.GetProperty("model").GetString().ShouldBe(AiFakes.GroqMain);
        GroqPrompt(handler).ShouldNotContain(Phone);
        GroqPrompt(handler).ShouldContain("[PHONE_1]");
    }

    [Fact]
    public async Task Bot_reply_from_groq_without_source_price_is_still_blocked()
    {
        const string reply = """
            {"messages":["Dạ gói này giá 300.000đ ạ."],"customer_salutation":"none","used_chunk_ids":[],"confidence":"high",
             "needs_human":false,"handoff_reason":null,"urgency":"none","health_topic":false,"sentiment":"neutral","lead_fields":[],"booking":null}
            """;
        var (chat, _) = AiFakes.GeminiOutGroqAnswers(_ => reply);
        var engine = new BotEngine(chat, new StaticKnowledge(), Microsoft.Extensions.Options.Options.Create(new AiOptions()), TimeProvider.System, NullLogger<BotEngine>.Instance);

        var result = await engine.ReplyAsync(new BotTurnRequest(Guid.NewGuid(), Spa, "gói gội đầu bao nhiêu", [], null, false, null), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.Reply.ShouldNotContain("300.000");
    }

    [Fact]
    public async Task Bot_hands_off_when_every_provider_fails()
    {
        var (chat, _, _) = AiFakes.Chain((_, _) => AiFakes.Status(System.Net.HttpStatusCode.TooManyRequests));
        var engine = new BotEngine(chat, new StaticKnowledge(Massage), Microsoft.Extensions.Options.Options.Create(new AiOptions()), TimeProvider.System, NullLogger<BotEngine>.Instance);

        var result = await engine.ReplyAsync(new BotTurnRequest(Guid.NewGuid(), Spa, "giá massage", [], null, false, null), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("ai_error");
        result.Messages.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Conversation_summary_from_groq_restores_pii()
    {
        var (chat, handler) = AiFakes.GeminiOutGroqAnswers(_ => "- Khách để lại số [PHONE_1], hỏi giá massage.");
        var engine = new BotEngine(chat, new StaticKnowledge(), Microsoft.Extensions.Options.Options.Create(new AiOptions()), TimeProvider.System, NullLogger<BotEngine>.Instance);

        var result = await engine.SummarizeAsync(Guid.NewGuid(), null, [new BotHistoryTurn(MessageSender.Customer, $"số em {Phone}, massage bao nhiêu?")], CancellationToken.None);

        result.Summary.ShouldBe($"- Khách để lại số {Phone}, hỏi giá massage.");
        result.Usage.ShouldHaveSingleItem().Provider.ShouldBe("groq");
        GroqPrompt(handler).ShouldNotContain(Phone);
    }

    [Fact]
    public async Task Care_analysis_from_groq_is_parsed()
    {
        var care = JsonSerializer.Serialize(new
        {
            action = "send",
            human_reason = (string?)null,
            promotional = false,
            temperature = "hot",
            trigger = "thinking",
            reason = "Chị [NAME_1] hỏi giá nhưng chưa đặt.",
            suggested_action = "Hỏi lại nhu cầu.",
            draft_message = "Dạ em chào chị, chị còn quan tâm gói massage không ạ?",
        });
        var (chat, handler) = AiFakes.GeminiOutGroqAnswers(_ => $"Kết quả:\n```json\n{care}\n```");
        var request = new CareAnalysisRequest(
            Guid.NewGuid(), Spa, "Nguyễn Thị Lan", null,
            [new BotHistoryTurn(MessageSender.Customer, "chị là Nguyễn Thị Lan, để chị suy nghĩ thêm")], [], LeadStatus.Interested, null, null);

        var result = await CareAdvisors.Create(chat).AnalyzeAsync(request, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Action.ShouldBe(CareAction.Send);
        result.Temperature.ShouldBe(CareTemperature.Hot);
        result.Reason.ShouldBe("Chị Nguyễn Thị Lan hỏi giá nhưng chưa đặt.");
        result.Draft.ShouldBe("Dạ em chào chị, chị còn quan tâm gói massage không ạ?");
        result.Usage.ShouldHaveSingleItem().Provider.ShouldBe("groq");
        GroqPrompt(handler).ShouldNotContain("Nguyễn Thị Lan");
    }

    [Fact]
    public async Task Appointment_brief_from_groq_restores_the_customer_name()
    {
        var (chat, handler) = AiFakes.GeminiOutGroqAnswers(_ => "- [NAME_1] đau vai gáy, thích lực nhẹ.");
        var writer = new AppointmentBriefWriter(chat);

        var result = await writer.WriteAsync(Guid.NewGuid(), new AppointmentBriefRequest(
            "Khoa Học Huyệt Đạo", "spa", "Nguyễn Thị Lan", ["Massage body 60 phút"], ["Nguyễn Thị Lan đau vai gáy, thích lực nhẹ"], null, [], []), CancellationToken.None);

        result.Brief.ShouldBe("- Nguyễn Thị Lan đau vai gáy, thích lực nhẹ.");
        result.Usage.ShouldHaveSingleItem().Provider.ShouldBe("groq");
        GroqPrompt(handler).ShouldNotContain("Nguyễn Thị Lan");
    }

    [Fact]
    public async Task Report_question_from_groq_is_parsed_even_with_a_json_fence()
    {
        var (chat, _) = AiFakes.GeminiOutGroqAnswers(_ => """
            ```json
            {"report":"customers","from":"2026-10-01","to":"2026-10-31","group_by":null,"focus":"khách chi nhiều nhất","unanswerable":false,"explanation":null}
            ```
            """);

        var (query, usage) = await new ReportQuestionInterpreter(chat).InterpretAsync("tháng 10 khách nào chi nhiều nhất", DateTimeOffset.Now, CancellationToken.None);

        query.Report.ShouldBe("customers");
        query.From.ShouldBe("2026-10-01");
        query.Unanswerable.ShouldBeFalse();
        usage.ShouldHaveSingleItem().Provider.ShouldBe("groq");
    }

    [Fact]
    public void Report_parser_reads_fenced_json_directly()
    {
        var query = ReportQuestionInterpreter.Parse("```json\n{\"report\":\"staff\",\"unanswerable\":false}\n```");

        query.Report.ShouldBe("staff");
        query.Unanswerable.ShouldBeFalse(); // trước đây chữ "json" sót lại làm hỏng JSON → "không hiểu câu hỏi"
    }

    [Fact]
    public async Task Report_narration_masks_customer_names_before_groq()
    {
        var (chat, handler) = AiFakes.GeminiOutGroqAnswers(_ => "Khách chi nhiều nhất là [NAME_1] với 5.000.000 đ.");
        var data = """{"topCustomers":[{"name":"Trần Văn Bình","amount":5000000}]}""";

        var (answer, usage) = await new ReportQuestionInterpreter(chat).NarrateAsync("khách nào chi nhiều nhất", data, ["Trần Văn Bình"], CancellationToken.None);

        answer.ShouldBe("Khách chi nhiều nhất là Trần Văn Bình với 5.000.000 đ.");
        usage.ShouldHaveSingleItem().Provider.ShouldBe("groq");
        GroqPrompt(handler).ShouldNotContain("Trần Văn Bình");
    }
}
