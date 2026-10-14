using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ZaloAi.Ai.Bot;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Ai;

/// <summary>Trả lời nhiều tin ngắn + xưng hô theo từng khách (chủ dự án chốt 2026-10-14).</summary>
public sealed class BotMessagesTests
{
    private sealed class ScriptedChat(Func<ChatRequest, string> answer) : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatResult(answer(request), "test", "test-model", 100, 20, 0.001m, 5));
        }
    }

    private sealed class StaticSearch(params KnowledgeHit[] hits) : IKnowledgeSearch
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>(hits);
    }

    private static readonly KnowledgeHit Massage = new(Guid.NewGuid(), "item", Guid.NewGuid(), "DV-MS60", "Massage body 60 phút", "Massage body 60 phút: 450.000đ. Không đau, thư giãn.", 0.8, false);

    private static readonly BotProfile Spa = new("Khoa Học Huyệt Đạo", "spa", "Huyệt Đạo", "em", BotTone.Human, null, "https://example.vn/bao-mat");

    private static BotEngine Engine(IChatProvider chat, IKnowledgeSearch search) =>
        new(chat, search, Microsoft.Extensions.Options.Options.Create(new AiOptions()), TimeProvider.System, NullLogger<BotEngine>.Instance);

    private static string MessagesJson(string[] messages, string[]? used = null, string salutation = "none", bool health = false) =>
        JsonSerializer.Serialize(new
        {
            messages,
            customer_salutation = salutation,
            used_chunk_ids = used ?? [],
            confidence = "high",
            needs_human = false,
            handoff_reason = (string?)null,
            urgency = "none",
            health_topic = health,
            sentiment = "positive",
            lead_fields = Array.Empty<object>(),
        });

    private static BotTurnRequest TurnWith(string message, string? salutation, bool byStaff, bool first = false) =>
        new(Guid.NewGuid(), Spa, message, [], null, first, null, salutation, byStaff);

    [Fact]
    public async Task Several_short_messages_are_kept_in_order_with_disclosure_as_its_own_first_message()
    {
        var chat = new ScriptedChat(_ => MessagesJson(["Dạ gói này giá 450.000đ ạ 😊", "  ", "Massage body 60 phút, thư giãn mà không đau.", "Mình muốn em giữ lịch giờ nào ạ?"], ["C1"]));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(TurnWith("giá massage 60p", null, false, first: true), CancellationToken.None);

        result.Messages.Count.ShouldBe(4); // câu báo AI + 3 tin (tin rỗng bị bỏ)
        result.Messages[0].ShouldStartWith("Dạ em là Huyệt Đạo, trợ lý AI");
        result.Messages[1].ShouldBe("Dạ gói này giá 450.000đ ạ 😊");
        result.Messages[3].ShouldBe("Mình muốn em giữ lịch giờ nào ạ?");
        JsonDocument.Parse(result.TraceJson).RootElement.GetProperty("parts").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task More_than_four_messages_are_merged_into_the_last_one()
    {
        var chat = new ScriptedChat(_ => MessagesJson(["1", "2", "3", "4", "5"]));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(TurnWith("hi", null, false), CancellationToken.None);

        result.Messages.ShouldBe(["1", "2", "3", "4\n5"]);
    }

    [Fact]
    public async Task Price_without_source_in_any_message_still_hands_off()
    {
        var chat = new ScriptedChat(_ => MessagesJson(["Dạ có ạ 😊", "Gói này 300k thôi ạ"]));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(TurnWith("giá sao", null, false), CancellationToken.None);

        result.NeedsHuman.ShouldBeTrue();
        result.HandoffReason.ShouldBe("no_knowledge");
        result.Messages.ShouldHaveSingleItem().ShouldNotContain("300k");
    }

    [Fact]
    public async Task Medical_disclaimer_is_added_as_an_extra_message()
    {
        var chat = new ScriptedChat(_ => MessagesJson(["Dạ đau vai gáy bên em có liệu trình bấm huyệt ạ."], ["C1"], health: true));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(TurnWith("đau vai gáy làm gì", "chị", true), CancellationToken.None);

        result.Messages.Count.ShouldBe(2);
        result.Messages[1].ShouldBe("Nếu chị thấy cảm giác này kéo dài hoặc khó chịu hơn, chị ghé Khoa Học Huyệt Đạo để chuyên viên bên em kiểm tra trực tiếp cho chị nha.");
    }

    [Theory]
    [InlineData("Dạ nếu còn mỏi, chị ghé spa để chuyên viên bên em kiểm tra trực tiếp nha 🌿")]
    [InlineData("Dạ với thoát vị đĩa đệm chị nên hỏi ý kiến bác sĩ điều trị trước ạ.")]
    public async Task No_extra_disclaimer_when_the_reply_already_invites_an_in_person_check(string reply)
    {
        var chat = new ScriptedChat(_ => MessagesJson([reply], ["C1"], health: true));
        var result = await Engine(chat, new StaticSearch(Massage)).ReplyAsync(TurnWith("hơi mỏi", "chị", true), CancellationToken.None);

        result.Messages.ShouldHaveSingleItem();
        var system = chat.Requests[0].System;
        system.ShouldContain("CHUYÊN VIÊN của doanh nghiệp kiểm tra trực tiếp");
        system.ShouldContain("KHÔNG khuyên đi khám bác sĩ cho những cảm giác này");
    }

    [Fact]
    public async Task Unknown_salutation_uses_minh_and_never_anh_chi()
    {
        var chat = new ScriptedChat(_ => "không phải json");
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(TurnWith("alo", null, false), CancellationToken.None);

        result.Reply.ShouldContain("chuyển mình sang nhân viên");
        var system = chat.Requests[0].System;
        system.ShouldContain("gọi khách là \"mình\"");
        system.ShouldNotContain("gọi khách là \"anh/chị\"");
    }

    [Fact]
    public async Task Staff_salutation_is_used_in_prompt_and_fixed_texts()
    {
        var chat = new ScriptedChat(_ => "không phải json");
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(TurnWith("alo", "chị", true), CancellationToken.None);

        result.Reply.ShouldContain("chuyển chị sang nhân viên");
        chat.Requests[0].System.ShouldContain("Nhân viên đã xác định: gọi khách là \"chị\"");
    }

    [Theory]
    [InlineData("chị", "chị")]
    [InlineData("cô", "cô")]
    [InlineData("none", null)]
    [InlineData("em", null)]
    public async Task Customer_self_reference_is_reported_only_for_allowed_values(string fromAi, string? expected)
    {
        var chat = new ScriptedChat(_ => MessagesJson(["Dạ em chào ạ 😊"], salutation: fromAi));
        var result = await Engine(chat, new StaticSearch()).ReplyAsync(TurnWith("chị muốn hỏi chút", null, false), CancellationToken.None);

        result.DetectedSalutation.ShouldBe(expected);
    }

    [Fact]
    public void Human_tone_limits_filler_words_and_emoji()
    {
        var guide = PromptBuilder.ToneGuide(BotTone.Human);
        guide.ShouldContain("1–4 tin ngắn");
        guide.ShouldContain("\"nè\" tối đa 1 lần");
        guide.ShouldContain("tổng 0–3");
    }
}
