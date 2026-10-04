using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ZaloAi.Ai.Bot;
using ZaloAi.Ai.Safety;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.UnitTests.Ai;

public sealed class BotEngineTests
{
    /// <summary>AI giả: trả lần lượt các câu trong kịch bản, ghi lại request.</summary>
    private sealed class ScriptedChat(params Func<ChatRequest, string>[] script) : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var text = script[Math.Min(Requests.Count - 1, script.Length - 1)](request);
            return Task.FromResult(new ChatResult(text, "test", "test-model", 100, 20, 0.001m, 5));
        }
    }

    private sealed class ThrowingChat : IChatProvider
    {
        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken) => throw new AiOverloadedException();
    }

    private sealed class StaticSearch(params KnowledgeHit[] hits) : IKnowledgeSearch
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<KnowledgeHit>>(hits);
        }
    }

    private static readonly KnowledgeHit Massage = new(Guid.NewGuid(), "item", Guid.NewGuid(), "DV-MS60", "Massage body 60 phút", "Massage body 60 phút: 450.000đ. Không đau, thư giãn.", 0.8, false);
    private static readonly KnowledgeHit Irrelevant = new(Guid.NewGuid(), "item", Guid.NewGuid(), "X", "Xa", "Không liên quan", 0.1, false);

    private static readonly BotProfile Spa = new("Khoa Học Huyệt Đạo", "spa", "Huyệt Đạo", "em", BotTone.Friendly, null, "https://example.vn/bao-mat");

    private static string Json(
        string reply,
        string[]? used = null,
        string confidence = "high",
        bool needsHuman = false,
        string urgency = "none",
        bool health = false,
        string? reason = null,
        object[]? leads = null) =>
        JsonSerializer.Serialize(new
        {
            reply,
            used_chunk_ids = used ?? [],
            confidence,
            needs_human = needsHuman,
            handoff_reason = reason,
            urgency,
            health_topic = health,
            sentiment = "neutral",
            lead_fields = leads ?? [],
        });

    private static BotEngine Engine(IChatProvider chat, IKnowledgeSearch search) =>
        new(chat, search, Microsoft.Extensions.Options.Options.Create(new AiOptions()), TimeProvider.System, NullLogger<BotEngine>.Instance);

    private static BotTurnRequest Turn(string message, bool first = false, IReadOnlyList<BotHistoryTurn>? history = null, string? name = null) =>
        new(Guid.NewGuid(), Spa, message, history ?? [], null, first, name);

    [Fact]
    public async Task Answers_from_knowledge_and_filters_low_score_chunks()
    {
        var chat = new ScriptedChat(_ => Json("Dạ massage body 60 phút giá 450.000đ ạ.", ["C1"]));
        var result = await Engine(chat, new StaticSearch(Massage, Irrelevant)).ReplyAsync(Turn("giá massage 60p"), CancellationToken.None);

        result.Reply.ShouldBe("Dạ massage body 60 phút giá 450.000đ ạ.");
        result.NeedsHuman.ShouldBeFalse();
        var system = chat.Requests.ShouldHaveSingleItem().System;
        system.ShouldContain("[C1] (mã DV-MS60 · chưa duyệt chuyên môn) Massage body 60 phút");
        system.ShouldNotContain("Không liên quan");

        var trace = JsonDocument.Parse(result.TraceJson).RootElement;
        trace.GetProperty("chunks")[0].GetProperty("used").GetBoolean().ShouldBeTrue();
        trace.GetProperty("belowThreshold").GetInt32().ShouldBe(1);
        result.Usage.ShouldHaveSingleItem().CostUsd.ShouldBe(0.001m);
    }

    [Fact]
    public async Task First_reply_has_ai_disclosure_and_privacy_link()
    {
        var chat = new ScriptedChat(_ => Json("Dạ em chào anh/chị ạ."));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(Turn("xin chào", first: true), CancellationToken.None);

        result.Reply.ShouldStartWith("Dạ em là Huyệt Đạo, trợ lý AI");
        result.Reply.ShouldContain("https://example.vn/bao-mat");
        result.Reply.ShouldEndWith("Dạ em chào anh/chị ạ.");
    }

    [Fact]
    public async Task Pii_is_masked_before_ai_and_restored_in_reply_and_leads()
    {
        var chat = new ScriptedChat(r => Json(
            "Dạ em ghi nhận số [PHONE_1] của chị [NAME_1] ạ.",
            needsHuman: true,
            reason: "booking",
            leads: [new { key = "phone", value = "[PHONE_1]" }, new { key = "cccd", value = "x" }]));
        var search = new StaticSearch();
        var result = await Engine(chat, search).ReplyAsync(Turn("Mình là Nguyễn Thị Lan, sđt 0912345678 nha", name: "Nguyễn Thị Lan"), CancellationToken.None);

        var sent = string.Join("\n", chat.Requests[0].Turns.Select(t => t.Text)) + chat.Requests[0].System;
        sent.ShouldNotContain("0912345678");
        sent.ShouldNotContain("Nguyễn Thị Lan");
        search.Queries.ShouldAllBe(q => !q.Contains("0912345678"));

        result.Reply.ShouldBe("Dạ em ghi nhận số 0912345678 của chị Nguyễn Thị Lan ạ.");
        result.LeadFields.ShouldBe(new Dictionary<string, string> { ["phone"] = "0912345678" }); // key lạ bị bỏ
        result.TraceJson.ShouldNotContain("0912345678");
    }

    [Fact]
    public async Task Danger_keyword_hands_off_urgently_without_calling_ai()
    {
        var chat = new ScriptedChat(_ => Json("không được gọi"));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("sau khi bấm huyệt em bị kho tho va dau nguc"), CancellationToken.None);

        chat.Requests.ShouldBeEmpty();
        result.Urgency.ShouldBe(Urgency.Urgent);
        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("urgent");
        result.Reply.ShouldContain("115");
        result.Reply.ShouldStartWith("Dạ anh/chị ơi");
    }

    [Fact]
    public async Task Ai_urgent_replaces_reply_with_emergency_text()
    {
        var chat = new ScriptedChat(_ => Json("Chị thử nghỉ ngơi nhé", urgency: "urgent"));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(Turn("người em lạ lắm"), CancellationToken.None);

        result.Urgency.ShouldBe(Urgency.Urgent);
        result.Reply.ShouldContain("115");
    }

    [Fact]
    public async Task Invalid_json_retries_once_then_hands_off()
    {
        var chat = new ScriptedChat(_ => "không phải json");
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("giá?"), CancellationToken.None);

        chat.Requests.Count.ShouldBe(2);
        chat.Requests[1].Temperature.ShouldBe(0);
        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("ai_error");
        result.Reply.ShouldContain("chuyển anh/chị sang nhân viên của Khoa Học Huyệt Đạo");
        result.Usage.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Forbidden_phrase_triggers_rewrite()
    {
        var chat = new ScriptedChat(
            _ => Json("Liệu trình này trị dứt điểm đau lưng ạ.", ["C1"]),
            _ => Json("Hiệu quả tùy cơ địa, chuyên viên sẽ tư vấn cụ thể ạ.", ["C1"]));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("có hết đau lưng không"), CancellationToken.None);

        chat.Requests.Count.ShouldBe(2);
        chat.Requests[1].Turns[^1].Text.ShouldContain("trị dứt điểm");
        result.Reply.ShouldBe("Hiệu quả tùy cơ địa, chuyên viên sẽ tư vấn cụ thể ạ.");
    }

    [Fact]
    public async Task Forbidden_phrase_twice_is_blocked()
    {
        var chat = new ScriptedChat(_ => Json("Cam kết hiệu quả 100% ạ", ["C1"]));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("có hiệu quả không"), CancellationToken.None);

        result.Reply.ShouldNotContain("Cam kết");
        result.HandoffReason.ShouldBe("forbidden_phrase");
    }

    [Fact]
    public async Task Non_strict_phrase_allowed_when_quoted_from_used_chunk()
    {
        var chat = new ScriptedChat(_ => Json("Dạ massage nhẹ nhàng, không đau ạ.", ["C1"]));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("có đau không"), CancellationToken.None);

        chat.Requests.Count.ShouldBe(1);
        result.Reply.ShouldBe("Dạ massage nhẹ nhàng, không đau ạ.");
    }

    [Fact]
    public async Task Price_without_source_chunk_is_not_sent()
    {
        var chat = new ScriptedChat(_ => Json("Dạ gói gội đầu dưỡng sinh 150k ạ."));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(Turn("gội đầu giá sao"), CancellationToken.None);

        result.Reply.ShouldNotContain("150k");
        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("no_knowledge");
    }

    [Fact]
    public async Task Low_confidence_forces_handoff()
    {
        var chat = new ScriptedChat(_ => Json("Dạ em chưa có thông tin này ạ.", confidence: "low"));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(Turn("có bán vé máy bay không"), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("low_confidence");
    }

    [Fact]
    public async Task Health_topic_gets_examination_disclaimer()
    {
        var chat = new ScriptedChat(_ => Json("Dạ bên em có massage cổ vai gáy giúp thư giãn ạ.", ["C1"], health: true));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("em hay đau cổ"), CancellationToken.None);

        result.Reply.ShouldContain("thăm khám trực tiếp");
    }

    [Fact]
    public async Task Ai_failure_returns_fallback_instead_of_throwing()
    {
        var result = await Engine(new ThrowingChat(), new StaticSearch(Massage)).ReplyAsync(Turn("giá?"), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("ai_error");
    }

    [Fact]
    public async Task Short_message_search_includes_previous_customer_turn()
    {
        var search = new StaticSearch(Massage);
        var chat = new ScriptedChat(_ => Json("ok", ["C1"]));
        await Engine(chat, search).ReplyAsync(
            Turn("còn 90p?", history: [new(MessageSender.Customer, "giá massage body"), new(MessageSender.Bot, "450k ạ")]),
            CancellationToken.None);

        search.Queries.ShouldHaveSingleItem().ShouldBe("giá massage body\ncòn 90p?");
        var turns = chat.Requests[0].Turns;
        turns.Select(t => t.Role).ShouldBe([ChatRole.User, ChatRole.Assistant, ChatRole.User]);
    }

    [Fact]
    public async Task Summary_masks_and_restores_pii()
    {
        var chat = new ScriptedChat(r => r.Turns[0].Text.Contains("[PHONE_1]", StringComparison.Ordinal) ? "- Khách để số [PHONE_1]" : "lộ số");
        var result = await Engine(chat, new StaticSearch()).SummarizeAsync(Guid.NewGuid(), null, [new(MessageSender.Customer, "số mình 0912345678")], CancellationToken.None);

        result.Summary.ShouldBe("- Khách để số 0912345678");
        chat.Requests[0].JsonSchema.ShouldBeNull();
    }
}

public sealed class IndustryTemplateTests
{
    [Fact]
    public void Spa_template_loads_with_medical_safety()
    {
        var spa = IndustryTemplateRegistry.Get("spa");
        spa.MedicalSafety.ShouldBeTrue();
        spa.Risk.ShouldBe("high");
        spa.Forbidden.ShouldContain(f => f.Phrase == "trị dứt điểm" && f.Strict);
        spa.DangerSignals.Count.ShouldBeGreaterThan(10);
        spa.LeadFields.ShouldContain(f => f.Key == "phone" && f.Pii);
        spa.UrgentReply.ShouldContain("115");
    }

    [Fact]
    public void Unknown_or_unwritten_industry_falls_back_to_default()
    {
        IndustryTemplateRegistry.Get("sua-nha").Slug.ShouldBe(IndustryTemplateRegistry.DefaultSlug);
        IndustryTemplateRegistry.Get(null).Slug.ShouldBe(IndustryTemplateRegistry.DefaultSlug);
        IndustryTemplateRegistry.HasOwnTemplate("spa").ShouldBeTrue();
    }

    [Theory]
    [InlineData("em bị khó thở quá", "kho_tho")]
    [InlineData("EM BI KHO THO", "kho_tho")]
    [InlineData("chỗ bấm bị sưng to và có mủ", "sung_bam")]
    [InlineData("tôi không muốn sống nữa", "tu_hai")]
    public void Detects_danger_signals_with_or_without_diacritics(string message, string signal) =>
        DangerDetector.Detect(IndustryTemplateRegistry.Get("spa"), message).ShouldBe(signal);

    [Theory]
    [InlineData("giá massage bao nhiêu")]
    [InlineData("thở dài vì giá cao quá")]
    [InlineData("đau lưng mỏi vai")]
    [InlineData("ngắt kết nối hoài")]
    [InlineData("chị đợi em xíu nha")]
    [InlineData("giảm xíu được không")]
    [InlineData("em không nói được tiếng Anh")]
    public void Normal_questions_are_not_danger(string message) =>
        DangerDetector.Detect(IndustryTemplateRegistry.Get("spa"), message).ShouldBeNull();

    [Fact]
    public void Forbidden_matching_respects_diacritics_and_word_boundaries()
    {
        var spa = IndustryTemplateRegistry.Get("spa");
        ForbiddenFilter.FindViolations(spa, "Không đâu ạ, chị yên tâm", []).ShouldBeEmpty();
        ForbiddenFilter.FindViolations(spa, "Hoàn tiền 1000%", []).ShouldBeEmpty();
        ForbiddenFilter.FindViolations(spa, "Hoàn tiền 100% nếu hủy trước 24h", ["Hoàn tiền 100% nếu hủy trước 24h"]).ShouldBeEmpty();
        ForbiddenFilter.FindViolations(spa, "Hoàn tiền 100%", []).ShouldBe(["100%"]);
        ForbiddenFilter.FindViolations(spa, "Trị DỨT ĐIỂM", ["trị dứt điểm"]).ShouldBe(["trị dứt điểm"]);
    }

    [Theory]
    [InlineData("Giá 450.000đ", true)]
    [InlineData("chỉ 150k thôi", true)]
    [InlineData("2 triệu", true)]
    [InlineData("60 phút", false)]
    [InlineData("3 đêm", false)]
    [InlineData("mở cửa 9h", false)]
    public void Price_detector(string text, bool expected) => PriceDetector.MentionsPrice(text).ShouldBe(expected);
}
