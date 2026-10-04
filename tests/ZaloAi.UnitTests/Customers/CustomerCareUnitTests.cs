using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Care;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;

namespace ZaloAi.UnitTests.Customers;

public sealed class LeadRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_promotes_never_demotes()
    {
        var contact = new Contact { ExternalUserId = "u", LeadStatus = LeadStatus.Hot };
        LeadRules.Promote(contact, LeadStatus.Interested, Now).ShouldBeFalse();
        contact.LeadStatus.ShouldBe(LeadStatus.Hot);

        var fresh = new Contact { ExternalUserId = "u" };
        LeadRules.Promote(fresh, LeadStatus.Interested, Now).ShouldBeTrue();
        fresh.LeadStatus.ShouldBe(LeadStatus.Interested);
        fresh.LeadStatusChangedAt.ShouldBe(Now);
    }

    [Fact]
    public void Never_touches_manual_or_closed_contacts()
    {
        var manual = new Contact { ExternalUserId = "u", LeadStatusManual = true };
        LeadRules.Promote(manual, LeadStatus.Hot, Now).ShouldBeFalse();

        var lost = new Contact { ExternalUserId = "u", LeadStatus = LeadStatus.Lost };
        LeadRules.Promote(lost, LeadStatus.Hot, Now).ShouldBeFalse();

        var fresh = new Contact { ExternalUserId = "u" };
        LeadRules.Promote(fresh, LeadStatus.Won, Now).ShouldBeFalse(); // "Đã chốt" chỉ nhân viên đặt
    }

    [Fact]
    public void Bot_turn_rules()
    {
        LeadRules.FromBotTurn(new Dictionary<string, string> { ["phone"] = "0912345678" }, null, 1).ShouldBe(LeadStatus.Hot);
        LeadRules.FromBotTurn(new Dictionary<string, string>(), "booking", 1).ShouldBe(LeadStatus.Hot);
        LeadRules.FromBotTurn(new Dictionary<string, string> { ["service_interest"] = "massage" }, null, 1).ShouldBe(LeadStatus.Interested);
        LeadRules.FromBotTurn(new Dictionary<string, string>(), null, 3).ShouldBe(LeadStatus.Interested);
        LeadRules.FromBotTurn(new Dictionary<string, string> { ["name"] = "Lan" }, null, 1).ShouldBe(LeadStatus.New);
    }
}

public sealed class TelegramLinkCommandTests
{
    [Theory]
    [InlineData("/ketnoi K7Q2MX", "K7Q2MX")]
    [InlineData("/ketnoi k7q2mx", "K7Q2MX")]
    [InlineData("/ketnoi@ZaloAiBot K7Q2MX", "K7Q2MX")]
    [InlineData("/start@ZaloAiBot K7Q2MX", "K7Q2MX")]
    [InlineData("  /KETNOI   ABC234  ", "ABC234")]
    [InlineData("/ketnoi", "")]
    public void Parses_link_commands(string text, string expected) => TelegramLinkCommand.TryParse(text).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("xin chào")]
    [InlineData("ketnoi ABC234")]
    [InlineData("/help")]
    [InlineData("/ketnoi ABC 234")]
    public void Ignores_other_messages(string? text) => TelegramLinkCommand.TryParse(text).ShouldBeNull();

    [Fact]
    public void New_codes_use_unambiguous_alphabet()
    {
        for (var i = 0; i < 50; i++)
        {
            var code = TelegramLinkCommand.NewCode();
            code.Length.ShouldBe(TelegramLinkCommand.CodeLength);
            code.ShouldAllBe(c => TelegramLinkCommand.Alphabet.Contains(c));
        }
    }
}

public sealed class CareAdvisorTests
{
    private sealed class ScriptedChat(params string[] answers) : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatResult(answers[Math.Min(Requests.Count - 1, answers.Length - 1)], "test", "m", 10, 5, 0.001m, 1));
        }
    }

    private static CareAnalysisRequest Request(string customerName = "Nguyễn Thị Lan") => new(
        Guid.NewGuid(),
        new BotProfile("Spa Hoa Mai", "spa", "Mai", "em", BotTone.Friendly, null, null),
        customerName,
        null,
        [new BotHistoryTurn(MessageSender.Customer, $"Chị là {customerName}, số chị 0912345678, gói triệt lông bao nhiêu em?"), new BotHistoryTurn(MessageSender.Bot, "Dạ 500k ạ")],
        [new CareNoteInput("service", new DateOnly(2026, 10, 1), null, "Đã làm thử 1 buổi")],
        LeadStatus.Interested,
        new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero),
        null);

    private static string Answer(string draft, bool follow = true, string action = "send") => JsonSerializer.Serialize(new
    {
        action = follow ? action : "none",
        human_reason = action == "ask_human" ? "complaint" : null,
        promotional = false,
        temperature = "hot",
        trigger = "price_no_close",
        reason = "[NAME_1] hỏi giá chưa đặt",
        suggested_action = "Mời đặt lịch",
        draft_message = draft,
    });

    [Fact]
    public async Task Masks_pii_before_ai_and_restores_in_result()
    {
        var chat = new ScriptedChat(Answer("Dạ chị [NAME_1] ơi, em nhắn hỏi thăm chị ạ."));
        var result = await new CareAdvisor(chat, TimeProvider.System).AnalyzeAsync(Request(), CancellationToken.None);

        var sent = chat.Requests.ShouldHaveSingleItem().Turns.ShouldHaveSingleItem().Text;
        sent.ShouldNotContain("0912345678");
        sent.ShouldNotContain("Nguyễn Thị Lan");
        result.ShouldNotBeNull();
        result.ShouldFollowUp.ShouldBeTrue();
        result.Temperature.ShouldBe(CareTemperature.Hot);
        result.Reason.ShouldBe("Nguyễn Thị Lan hỏi giá chưa đặt");
        result.Draft.ShouldBe("Dạ chị Nguyễn Thị Lan ơi, em nhắn hỏi thăm chị ạ.");
        result.Usage.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Invalid_output_retries_once_then_gives_up()
    {
        var chat = new ScriptedChat("không phải json", "{}");
        (await new CareAdvisor(chat, TimeProvider.System).AnalyzeAsync(Request(), CancellationToken.None)).ShouldBeNull();
        chat.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task No_follow_up_means_no_draft_and_unknown_trigger_becomes_other()
    {
        var json = Answer("Tin nháp", follow: false).Replace("price_no_close", "spam_everyone", StringComparison.Ordinal);
        var result = await new CareAdvisor(new ScriptedChat(json), TimeProvider.System).AnalyzeAsync(Request(), CancellationToken.None);
        result.ShouldNotBeNull();
        result.ShouldFollowUp.ShouldBeFalse();
        result.Draft.ShouldBeNull();
        result.Trigger.ShouldBe("other");
    }
}

public sealed class CareAdvisorBotVoiceTests
{
    private sealed class OneAnswer(string answer) : IChatProvider
    {
        public ChatRequest? Last { get; private set; }

        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new ChatResult(answer, "test", "m", 10, 5, 0.001m, 1));
        }
    }

    private static CareAnalysisRequest Request(bool botWillSend) => new(
        Guid.NewGuid(),
        new BotProfile("Spa Hoa Mai", "spa", "Mai", "em", BotTone.Friendly, null, null),
        null,
        null,
        [new BotHistoryTurn(MessageSender.Customer, "gói triệt lông bao nhiêu em?")],
        [],
        LeadStatus.Interested,
        null,
        null,
        botWillSend);

    private static string Json(string action, string draft, string? humanReason = null, bool promotional = false) => JsonSerializer.Serialize(new
    {
        action,
        human_reason = humanReason,
        promotional,
        temperature = "warm",
        trigger = "price_no_close",
        reason = "Hỏi giá chưa đặt",
        suggested_action = "Hỏi thăm",
        draft_message = draft,
    });

    [Fact]
    public async Task Bot_voice_prompt_and_price_in_bot_draft_is_blocked()
    {
        var chat = new OneAnswer(Json("send", "Dạ gói triệt lông bên em 500k ạ, chị muốn đặt lịch không?"));
        var result = await new CareAdvisor(chat, TimeProvider.System).AnalyzeAsync(Request(botWillSend: true), CancellationToken.None);

        chat.Last!.System.ShouldContain("TRỢ LÝ AI Mai");
        result.ShouldNotBeNull();
        result.Action.ShouldBe(CareAction.Send);
        result.DraftBlocked.ShouldBeTrue();
        result.Draft.ShouldBeNull();

        // Nhân viên gửi (không phải bot): giá do nhân viên kiểm tra, không chặn.
        var staff = await new CareAdvisor(new OneAnswer(Json("send", "Dạ gói 500k ạ")), TimeProvider.System).AnalyzeAsync(Request(botWillSend: false), CancellationToken.None);
        staff!.DraftBlocked.ShouldBeFalse();
    }

    [Fact]
    public async Task Parses_ask_human_and_promotional()
    {
        var result = await new CareAdvisor(new OneAnswer(Json("ask_human", "Dạ em hỏi thăm ạ", "weird_reason", promotional: true)), TimeProvider.System)
            .AnalyzeAsync(Request(botWillSend: true), CancellationToken.None);
        result!.Action.ShouldBe(CareAction.AskHuman);
        result.HumanReason.ShouldBe("other");
        result.Promotional.ShouldBeTrue();
    }
}

public sealed class CareDecisionTests
{
    // 10:00 giờ Việt Nam
    private static readonly DateTimeOffset Morning = new(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);

    private static CareAnalysisResult Send(string? draft = "Dạ em hỏi thăm ạ", bool promotional = false, bool blocked = false) =>
        new(CareAction.Send, CareTemperature.Warm, "thinking", "r", null, draft, [], null, promotional, blocked);

    private static Contact NewContact() => new() { ExternalUserId = "u" };

    private static Conversation Chat(ChannelKind channel = ChannelKind.Zalo, DateTimeOffset? lastCustomer = null) =>
        new() { Channel = channel, LastCustomerMessageAt = lastCustomer ?? Morning.AddHours(-7) };

    [Fact]
    public void Sends_now_inside_window()
    {
        var d = CareDecision.Decide(Send(), NewContact(), Chat(), new HandoffSettings(), isFollowUp: false, Morning);
        d.Kind.ShouldBe(CareDecisionKind.AutoSend);
        d.SendAt.ShouldBe(Morning);
    }

    [Fact]
    public void Night_is_postponed_to_next_morning_window()
    {
        var night = new DateTimeOffset(2026, 10, 5, 22, 30, 0, TimeSpan.FromHours(7));
        var d = CareDecision.Decide(Send(), NewContact(), Chat(lastCustomer: night.AddHours(-7)), new HandoffSettings(), false, night);
        d.Kind.ShouldBe(CareDecisionKind.AutoSend);
        d.SendAt.ShouldBe(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.FromHours(7)));
    }

    [Fact]
    public void Business_window_is_clamped_to_7_to_21()
    {
        var (start, end) = CareDecision.SendWindow(new HandoffSettings { CareSendStart = "05:00", CareSendEnd = "23:30" });
        start.ShouldBe(new TimeOnly(7, 0));
        end.ShouldBe(new TimeOnly(21, 0));
    }

    [Fact]
    public void One_unanswered_proactive_message_then_stop_except_follow_up_notes()
    {
        var contact = NewContact();
        contact.ProactiveAwaitingReply = true;
        contact.LastProactiveAt = Morning.AddDays(-2);
        CareDecision.Decide(Send(), contact, Chat(), new HandoffSettings(), false, Morning).Kind.ShouldBe(CareDecisionKind.Nothing);
        CareDecision.Decide(Send(), contact, Chat(), new HandoffSettings(), true, Morning).Kind.ShouldBe(CareDecisionKind.AutoSend);
    }

    [Fact]
    public void At_least_24h_between_proactive_messages()
    {
        var contact = NewContact();
        contact.LastProactiveAt = Morning.AddHours(-2);
        var d = CareDecision.Decide(Send(), contact, Chat(ChannelKind.Webchat), new HandoffSettings(), true, Morning);
        d.Kind.ShouldBe(CareDecisionKind.AutoSend);
        d.SendAt.ShouldBe(Morning.AddHours(22));
    }

    [Fact]
    public void Escalates_when_bot_must_not_send()
    {
        var s = new HandoffSettings();
        CareDecision.Decide(Send(promotional: true), NewContact(), Chat(), s, false, Morning).Reason.ShouldBe("promotional_no_consent");
        CareDecision.Decide(Send(draft: null, blocked: true), NewContact(), Chat(), s, false, Morning).Reason.ShouldBe("draft_blocked");
        CareDecision.Decide(Send(), NewContact(), new Conversation { Channel = ChannelKind.Zalo, Mode = ConversationMode.Human, LastCustomerMessageAt = Morning }, s, false, Morning)
            .Reason.ShouldBe("staff_handling");
        CareDecision.Decide(Send(), NewContact(), Chat(lastCustomer: Morning.AddDays(-7).AddMinutes(-1)), s, false, Morning).Reason.ShouldBe("outside_window");
        var ask = new CareAnalysisResult(CareAction.AskHuman, CareTemperature.Hot, "other", "r", null, "d", [], "complaint");
        CareDecision.Decide(ask, NewContact(), Chat(), s, false, Morning).ShouldBe(new CareDecisionResult(CareDecisionKind.Escalate, "complaint"));
    }

    [Fact]
    public void Opt_out_and_suggest_only_mode()
    {
        var optedOut = NewContact();
        optedOut.ProactiveOptOutAt = Morning;
        CareDecision.Decide(Send(), optedOut, Chat(), new HandoffSettings(), true, Morning).Kind.ShouldBe(CareDecisionKind.Nothing);
        CareDecision.Decide(Send(), NewContact(), Chat(), new HandoffSettings { CareAutoSend = false }, false, Morning).Kind.ShouldBe(CareDecisionKind.Suggest);
    }

    [Theory]
    [InlineData("Hủy", true)]
    [InlineData("  dừng nhắn tin!! ", true)]
    [InlineData("STOP", true)]
    [InlineData("Đừng nhắn nữa", true)]
    [InlineData("Đúng", false)]
    [InlineData("hủy lịch hẹn thứ 7", false)]
    [InlineData("gói 90 phút bao nhiêu", false)]
    public void Opt_out_command_matches_whole_message_only(string text, bool expected) =>
        CareDecision.IsOptOutCommand(text).ShouldBe(expected);
}
