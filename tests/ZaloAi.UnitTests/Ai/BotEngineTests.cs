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
        object[]? leads = null,
        string sentiment = "neutral") =>
        JsonSerializer.Serialize(new
        {
            reply,
            used_chunk_ids = used ?? [],
            confidence,
            needs_human = needsHuman,
            handoff_reason = reason,
            urgency,
            health_topic = health,
            sentiment,
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
        result.Usage.Single(u => u.Kind == UsageKind.Chat).CostUsd.ShouldBe(0.001m);
        result.Usage.Where(u => u.Kind == UsageKind.Embed).ShouldAllBe(u => u.InputTokens > 0); // tạo vector câu tìm kiếm cũng được ghi chi phí
        result.Usage.Count(u => u.Kind == UsageKind.Embed).ShouldBe(2); // tin ngắn: câu hỏi + nhóm dịch vụ tiêu biểu
    }

    [Fact]
    public async Task Thanks_message_skips_knowledge_search_but_still_answers()
    {
        var chat = new ScriptedChat(_ => Json("Dạ không có gì ạ, chúc mình một ngày vui nha 😊"));
        var search = new StaticSearch(Massage);

        var result = await Engine(chat, search).ReplyAsync(Turn("dạ em cảm ơn nhiều nha"), CancellationToken.None);

        search.Queries.ShouldBeEmpty();
        result.NeedsHuman.ShouldBeFalse();
        var system = chat.Requests.ShouldHaveSingleItem().System;
        system.ShouldContain("Tin xã giao — không cần dữ liệu");
        system.ShouldNotContain("[C1]");
        var trace = JsonDocument.Parse(result.TraceJson).RootElement;
        trace.GetProperty("guards").EnumerateArray().Select(g => g.GetString()).ShouldContain("small_talk_no_search");
        trace.GetProperty("inputTokens").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task First_reply_has_ai_disclosure_and_privacy_link()
    {
        var chat = new ScriptedChat(_ => Json("Dạ em chào anh/chị ạ."));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(Turn("xin chào", first: true), CancellationToken.None);

        result.Messages.Count.ShouldBe(1); // gộp vào tin đầu, không thành tin riêng
        result.Reply.ShouldStartWith("Dạ em là trợ lý AI của Khoa Học Huyệt Đạo ạ. Em chào");
        result.Reply.ShouldEndWith("\nChính sách bảo mật của Khoa Học Huyệt Đạo: https://example.vn/bao-mat");
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
        result.Reply.ShouldStartWith("Dạ, những dấu hiệu mình mô tả");
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
        result.Reply.ShouldNotContain("chuyển"); // câu chuyển tiếp do DN cài, hệ thống gửi sau (InboxService)
        result.Usage.Count(u => u.Kind == UsageKind.Chat).ShouldBe(2);
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

        result.Reply.ShouldContain("ghé Khoa Học Huyệt Đạo để chuyên viên bên em kiểm tra trực tiếp");
        result.Reply.ShouldNotContain("bác sĩ");
    }

    [Fact]
    public async Task Ai_failure_returns_fallback_instead_of_throwing()
    {
        var result = await Engine(new ThrowingChat(), new StaticSearch(Massage)).ReplyAsync(Turn("giá?"), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("ai_error");
    }

    [Fact]
    public async Task Max_messages_is_told_to_the_ai_only_when_set()
    {
        var chat = new ScriptedChat(_ => Json("ok", ["C1"]));
        await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("Cho mình hỏi giá massage body 60 phút bao nhiêu vậy?") with { MaxMessages = 2 }, CancellationToken.None);
        await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn("Cho mình hỏi giá massage body 60 phút bao nhiêu vậy?"), CancellationToken.None);

        chat.Requests[0].System.ShouldContain("Lượt này chỉ gửi TỐI ĐA 2 tin");
        chat.Requests[1].System.ShouldNotContain("Lượt này chỉ gửi");
    }

    [Fact]
    public async Task Short_message_search_includes_previous_customer_turn()
    {
        var search = new StaticSearch(Massage);
        var chat = new ScriptedChat(_ => Json("ok", ["C1"]));
        await Engine(chat, search).ReplyAsync(
            Turn("còn 90p?", history: [new(MessageSender.Customer, "giá massage body"), new(MessageSender.Bot, "450k ạ")]),
            CancellationToken.None);

        search.Queries[0].ShouldBe("giá massage body\n450k ạ\ncòn 90p?");
        var turns = chat.Requests[0].Turns;
        turns.Select(t => t.Role).ShouldBe([ChatRole.User, ChatRole.Assistant, ChatRole.User]);
    }

    private sealed class QuerySearch(Func<string, KnowledgeHit[]> hits) : IKnowledgeSearch
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<KnowledgeHit>>(hits(query));
        }
    }

    [Fact]
    public async Task Short_yes_searches_what_the_bot_offered_and_adds_featured_services()
    {
        // Lỗi thật 2026-11-14: bot "em gửi thông tin các gói nha", khách "có" → tìm theo "còn gì không\ncó" ra toàn chính sách,
        // bot không có gì cụ thể để gửi nên hỏi lại chung chung.
        var policy = new KnowledgeHit(Guid.NewGuid(), "item", Guid.NewGuid(), "CS-1", "Đặt lịch", "Đặt lịch qua hotline.", 0.6, false);
        var package = new KnowledgeHit(Guid.NewGuid(), "item", Guid.NewGuid(), "GOI-1", "Gói cổ vai gáy", "Gói cổ vai gáy 10 buổi: 3.500.000đ", 0.6, false);
        var search = new QuerySearch(q => q.StartsWith("Dịch vụ, gói", StringComparison.Ordinal) ? [Massage, package, policy] : [policy]);
        var chat = new ScriptedChat(_ => Json("Dạ bên em có massage body 60 phút 450.000đ ạ.", ["C2"]));
        await Engine(chat, search).ReplyAsync(
            Turn("có", history:
            [
                new(MessageSender.Customer, "còn gì không"),
                new(MessageSender.Bot, "Dạ, em chỉ muốn gửi thông tin các gói chăm sóc sức khỏe để mình tham khảo thôi ạ"),
            ]),
            CancellationToken.None);

        search.Queries[0].ShouldBe("còn gì không\nDạ, em chỉ muốn gửi thông tin các gói chăm sóc sức khỏe để mình tham khảo thôi ạ\ncó");
        search.Queries.Count.ShouldBe(2);
        var system = chat.Requests[0].System;
        system.ShouldContain("Massage body 60 phút: 450.000đ");
        system.ShouldContain("Gói cổ vai gáy 10 buổi");
        system.Split("Đặt lịch qua hotline.").Length.ShouldBe(2); // không trùng đoạn
        system.ShouldContain("làm luôn điều đó");
    }

    [Fact]
    public async Task Long_question_does_one_plain_search()
    {
        var search = new StaticSearch(Massage);
        await Engine(new ScriptedChat(_ => Json("ok", ["C1"])), search).ReplyAsync(
            Turn("Cho mình hỏi massage body 60 phút giá bao nhiêu vậy?", history: [new(MessageSender.Bot, "Dạ em chào mình")]),
            CancellationToken.None);

        search.Queries.ShouldHaveSingleItem().ShouldBe("Cho mình hỏi massage body 60 phút giá bao nhiêu vậy?");
    }

    [Fact]
    public async Task Business_first_message_reaches_ai_when_customer_replies_to_it()
    {
        // Zalo cá nhân: bot chủ động chào trước, khách đáp "Sao á e". Trước đây tin chào bị bỏ (lượt chat phải bắt đầu bằng khách).
        var search = new StaticSearch(Massage);
        var chat = new ScriptedChat(_ => Json("Dạ bên em là spa trị liệu, được người quen của chị giới thiệu ạ."));
        await Engine(chat, search).ReplyAsync(
            Turn("Sao á e", history: [new(MessageSender.Bot, "Dạ em chào Lan Anh! Bên em được người quen của Lan Anh giới thiệu ạ.")], name: "Lan Anh"),
            CancellationToken.None);

        var request = chat.Requests[0];
        request.Turns.ShouldHaveSingleItem().Text.ShouldBe("Sao á e");
        request.System.ShouldContain("BÊN MÌNH ĐÃ NHẮN KHÁCH TRƯỚC");
        request.System.ShouldContain("Bên em được người quen của [NAME_1] giới thiệu ạ.");
        request.System.ShouldNotContain("Lan Anh");
        search.Queries[0].ShouldEndWith("\nSao á e");
    }

    [Fact]
    public async Task Leading_bot_message_after_summary_is_context_not_conversation_start()
    {
        var chat = new ScriptedChat(_ => Json("Dạ vâng ạ."));
        await Engine(chat, new StaticSearch()).ReplyAsync(
            new BotTurnRequest(Guid.NewGuid(), Spa, "ok e", [new(MessageSender.Staff, "Mai 9h chị ghé được không ạ?")], "- Khách hỏi giá massage", false),
            CancellationToken.None);

        var system = chat.Requests[0].System;
        system.ShouldContain("TIN BÊN MÌNH GỬI NGAY TRƯỚC TIN CỦA KHÁCH");
        system.ShouldContain("(Nhân viên) Mai 9h chị ghé được không ạ?");
        system.ShouldNotContain("BÊN MÌNH ĐÃ NHẮN KHÁCH TRƯỚC");
    }

    [Fact]
    public async Task No_opening_section_when_customer_spoke_first()
    {
        var chat = new ScriptedChat(_ => Json("ok", ["C1"]));
        await Engine(chat, new StaticSearch(Massage)).ReplyAsync(
            Turn("còn 90p?", history: [new(MessageSender.Customer, "giá massage body"), new(MessageSender.Bot, "450k ạ")]),
            CancellationToken.None);

        chat.Requests[0].System.ShouldNotContain("TIN BÊN MÌNH");
        chat.Requests[0].System.ShouldNotContain("NHẮN KHÁCH TRƯỚC");
    }

    [Fact]
    public async Task Negative_sentiment_hands_off_even_when_ai_answers()
    {
        var chat = new ScriptedChat(_ => Json("Dạ em xin lỗi chị vì trải nghiệm chưa tốt ạ.", sentiment: "negative"));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(Turn("dịch vụ tệ quá, chờ cả tiếng"), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("negative_sentiment");
    }

    [Fact]
    public async Task Media_message_hands_off_without_calling_ai()
    {
        var chat = new ScriptedChat(_ => Json("không được gọi"));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn(ZaloAi.Core.Channels.InboundText.ForAttachment("image")!), CancellationToken.None);

        chat.Requests.ShouldBeEmpty();
        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("media");
        result.Reply.ShouldContain("chưa xem được hình ảnh");
    }

    [Fact]
    public async Task Sticker_with_meaning_is_answered_by_ai_in_context()
    {
        // 2026-11-25: sticker không còn câu trả lời cố định — job ghi nghĩa vào tin, AI hiểu theo ngữ cảnh (quy tắc 11c).
        var chat = new ScriptedChat(_ => Json("Dạ vậy em gửi mình bảng giá massage nha"));
        var sticker = ZaloAi.Core.Channels.InboundText.StickerMeaning("giơ ngón cái – đồng ý, ok");
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(Turn(sticker), CancellationToken.None);

        var request = chat.Requests.ShouldHaveSingleItem();
        request.System.ShouldContain("11c. Tin \"[Khách gửi sticker: …]\"");
        request.Turns[^1].Text.ShouldContain("giơ ngón cái");
        result.NeedsHuman.ShouldBeFalse();
        JsonDocument.Parse(result.TraceJson).RootElement.GetProperty("guards").EnumerateArray().Select(g => g.GetString()).ShouldContain("sticker");
    }

    [Fact]
    public void Sticker_text_helpers_recognise_both_forms_and_keep_the_marker_intact()
    {
        ZaloAi.Core.Channels.InboundText.IsSticker(ZaloAi.Core.Channels.InboundText.Sticker).ShouldBeTrue();
        ZaloAi.Core.Channels.InboundText.IsSticker("[Khách gửi sticker: vẫy tay – chào]").ShouldBeTrue();
        ZaloAi.Core.Channels.InboundText.IsSticker("ok em").ShouldBeFalse();
        ZaloAi.Core.Channels.InboundText.StickerMeaning(" [mèo] cười ").ShouldBe("[Khách gửi sticker: (mèo) cười]");
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
