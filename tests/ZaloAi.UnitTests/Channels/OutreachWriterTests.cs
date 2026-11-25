using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ZaloAi.Ai.Outreach;
using ZaloAi.Ai.Privacy;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.IndustryTemplates;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.UnitTests.Channels;

/// <summary>Tin chủ động do AI viết (chủ dự án chốt 2026-11-15): luật bắt buộc, che tên, dự phòng bằng mẫu.</summary>
public sealed class OutreachWriterTests
{
    private sealed class ScriptedChat(params string[] replies) : IChatProvider
    {
        private int _next;

        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var text = replies[Math.Min(_next++, replies.Length - 1)];
            return Task.FromResult(new ChatResult(text, "test", "test-model", 800, 60, 0.0005m, 5));
        }
    }

    private static readonly BotProfile Spa = new("Khoa Học Huyệt Đạo", "spa", "Huyệt Đạo", "em", BotTone.Human, null, null);

    private static string Json(params string[] messages) => JsonSerializer.Serialize(new { outreach_messages = messages });

    private static OutreachMessageRequest Request(OutreachMessageKind kind, string? name = "Lan Anh", string? referrer = "Hoa") =>
        new(Guid.NewGuid(), Spa, kind, name, "chị", "referral", referrer, "hay mỏi vai gáy", ["Trị liệu chuyên sâu", "Dưỡng sinh tạng"], []);

    private static OutreachWriter Writer(IChatProvider chat) => new(chat, NullLogger<OutreachWriter>.Instance);

    [Fact]
    public async Task Natural_greeting_passes_and_names_are_masked_for_the_ai()
    {
        var chat = new ScriptedChat(Json(
            "Dạ em chào chị [NAME_1] ạ 😊 Em là Huyệt Đạo, trợ lý AI bên Khoa Học Huyệt Đạo. Chị [NAME_2] có giới thiệu chị với bên em nên em xin phép làm quen ạ.",
            "Nghe nói dạo này chị hay mỏi vai gáy, chị có muốn em gửi vài gợi ý phù hợp không ạ?"));

        var result = await Writer(chat).WriteAsync(Request(OutreachMessageKind.Greeting), CancellationToken.None);

        result.Messages.Count.ShouldBe(2);
        result.Messages[0].ShouldContain("chị Lan Anh");
        result.Messages[0].ShouldContain("Chị Hoa có giới thiệu");
        var sent = chat.Requests.ShouldHaveSingleItem().Turns[0].Text;
        sent.ShouldNotContain("Lan Anh");
        sent.ShouldContain("chị [NAME_1]");
        sent.ShouldContain("[NAME_2] (khách của bên mình) giới thiệu");
        result.Usage.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Price_twice_falls_back_to_template()
    {
        var withPrice = Json("Dạ em là trợ lý AI của Khoa Học Huyệt Đạo, bên em có massage 490k ạ, chị muốn thử không ạ?");
        var chat = new ScriptedChat(withPrice, withPrice);

        var result = await Writer(chat).WriteAsync(Request(OutreachMessageKind.Greeting), CancellationToken.None);

        result.Messages.ShouldBeEmpty(); // nơi gọi dùng mẫu soạn sẵn
        chat.Requests.Count.ShouldBe(2);
        result.Usage.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Second_try_can_fix_a_missing_disclosure()
    {
        var chat = new ScriptedChat(
            Json("Dạ em chào chị [NAME_1], em bên Khoa Học Huyệt Đạo ạ."),
            Json("Dạ em chào chị [NAME_1], em là trợ lý AI bên Khoa Học Huyệt Đạo. Chị có muốn em gửi thông tin không ạ?"));

        var result = await Writer(chat).WriteAsync(Request(OutreachMessageKind.Greeting), CancellationToken.None);

        result.Messages.ShouldHaveSingleItem().ShouldContain("trợ lý AI");
    }

    private static string? Check(OutreachMessageKind kind, params string[] masked)
    {
        var vault = new PiiVault();
        vault.AddKnown(PiiMasker.Name, "Lan Anh");
        PiiMasker.Mask("Lan Anh", vault); // tạo [NAME_1]
        return OutreachWriter.Check(IndustryTemplateRegistry.Get("spa"), kind, masked, vault);
    }

    [Theory]
    [InlineData(OutreachMessageKind.Greeting, "Dạ em chào chị [NAME_1], em là trợ lý AI bên tiệm. Chị có muốn em gửi thông tin không ạ?", null)]
    [InlineData(OutreachMessageKind.Greeting, "Dạ em chào chị [NAME_1], em bên tiệm ạ.", "no_ai_disclosure")]
    [InlineData(OutreachMessageKind.AcceptedThanks, "Dạ em cảm ơn chị [NAME_1] đã kết bạn ạ, cần gì chị cứ nhắn em nha.", null)]
    [InlineData(OutreachMessageKind.Greeting, "Em là trợ lý AI, bên em đang có ưu đãi cho khách mới ạ.", "promo_or_salutation")]
    [InlineData(OutreachMessageKind.Greeting, "Em là trợ lý AI, chào anh/chị ạ.", "promo_or_salutation")]
    [InlineData(OutreachMessageKind.Greeting, "Em là trợ lý AI, bên em mở cửa tới 21 giờ ạ.", "number_or_price")]
    [InlineData(OutreachMessageKind.Greeting, "Em là trợ lý AI, chị [NAME_3] giới thiệu ạ.", "placeholder")]
    // Giới thiệu sau khi kết bạn (2026-11-25): tin chào đã báo trợ lý AI; phải mở câu hỏi, không kết kiểu chờ khách tự nhắn.
    [InlineData(OutreachMessageKind.AcceptedIntro, "Dạ em cảm ơn chị [NAME_1] đã kết bạn ạ. Bên em chuyên các liệu trình chăm sóc, chị đang quan tâm phần nào ạ?", null)]
    [InlineData(OutreachMessageKind.AcceptedIntro, "Dạ em cảm ơn chị [NAME_1] đã kết bạn ạ, cần gì chị cứ nhắn em nha.", "passive_close")]
    [InlineData(OutreachMessageKind.AcceptedIntro, "Cảm ơn chị [NAME_1] nha, khi nào cần thì liên hệ em ạ.", "passive_close")]
    public void Rules_are_checked_on_masked_text(OutreachMessageKind kind, string text, string? problem) =>
        Check(kind, text).ShouldBe(problem);

    [Fact]
    public void Invite_is_one_short_part()
    {
        Check(OutreachMessageKind.Invite, "Em là trợ lý AI bên tiệm ạ.", "Kết bạn với em nha.").ShouldBe("part_count");
        Check(OutreachMessageKind.Invite, "Em là trợ lý AI bên tiệm, " + new string('a', 150)).ShouldBe("too_long");
    }

    [Fact]
    public void Name_ending_in_anh_before_chi_is_not_a_banned_salutation() =>
        Check(OutreachMessageKind.Greeting, "Dạ em là trợ lý AI ạ, chị [NAME_1] chị cần gì em hỗ trợ nha.").ShouldBeNull();
}

public sealed class OutreachTemplateTests
{
    private static readonly string[] Offerings = ["Trị liệu chuyên sâu", "Dưỡng sinh tạng", "Gói dưỡng sinh"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_first_message_variant_discloses_ai_and_asks_permission(int variant)
    {
        foreach (var origin in new[] { "referral", "inquiry", "customer" })
        {
            var text = OutreachGreeting.FirstMessage("Hà", "em", "Spa An Nhiên", "chị", "Hoa", origin, origin == "referral" ? "chị Lan" : null, variant);
            text.ShouldContain("trợ lý AI");
            text.ShouldEndWith("không ạ?");
            text.Any(char.IsDigit).ShouldBeFalse();
            text.ShouldNotContain("anh/chị");
        }
    }

    [Fact]
    public void Accepted_templates_thank_and_say_what_the_business_helps_with()
    {
        for (var v = 0; v < OutreachGreeting.AcceptedVariants; v++)
        {
            var greeting = OutreachGreeting.AcceptedGreeting("Hà", "em", "Spa", "chị", "Hoa", Offerings, v);
            greeting.ShouldContain("trợ lý AI");
            greeting.ShouldContain("trị liệu chuyên sâu");
            greeting.ShouldContain("ạ?");
            var thanks = OutreachGreeting.AcceptedThanks("em", "chị", "Hoa", Offerings, v);
            thanks.ShouldContain("ảm ơn chị Hoa đã kết bạn");
            thanks.ShouldContain("dưỡng sinh tạng");
        }
    }

    [Fact]
    public void Intro_after_accepting_thanks_introduces_the_business_and_asks_an_open_question()
    {
        for (var v = 0; v < OutreachGreeting.AcceptedVariants; v++)
        {
            var intro = OutreachGreeting.AcceptedIntro("em", "Spa An Nhiên", "chị", "Hoa", Offerings, v);
            intro.ShouldContain("ảm ơn chị Hoa đã");
            intro.ShouldContain("Spa An Nhiên");
            intro.ShouldContain("trị liệu chuyên sâu");
            intro.ShouldEndWith("ạ?");
            intro.ShouldNotContain("cứ nhắn");
            intro.Any(char.IsDigit).ShouldBeFalse();
        }
    }

    [Fact]
    public void Offer_lists_up_to_three_groups() =>
        OutreachGreeting.Offer(["Trị liệu chuyên sâu", "Dưỡng sinh tạng", "Gói dưỡng sinh", "Đào tạo"])
            .ShouldBe("các liệu trình trị liệu chuyên sâu, dưỡng sinh tạng và gói dưỡng sinh");

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    [InlineData(10, 7)]
    public void New_people_leave_about_a_third_of_invites_for_people_who_replied(int perDay, int cold) =>
        OutreachTenantJob.ColdInviteCap(perDay).ShouldBe(cold);
}

public sealed class PersonalReplyPacingTests
{
    private static readonly ZaloPersonalOptions Options = new();

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    public void Night_crosses_midnight_in_vietnam_time(int hour, int minute, bool night)
    {
        var local = new DateTimeOffset(2026, 11, 15, hour, minute, 0, TimeSpan.FromHours(7));
        PersonalPacing.IsNight(Options, local.ToUniversalTime()).ShouldBe(night);
    }

    [Theory]
    [InlineData(0.0, false, 4)]
    [InlineData(0.69, false, 4)]
    [InlineData(0.7, false, 2)]
    [InlineData(0.89, false, 2)]
    [InlineData(0.9, false, 1)]
    [InlineData(0.1, true, 1)]
    public void Reply_parts_shrink_as_the_nick_uses_up_its_sends(double load, bool night, int parts) =>
        PersonalPacing.ReplyParts(load, night).ShouldBe(parts);

    [Fact]
    public void Extra_parts_are_merged_into_the_last_one()
    {
        MessageParts.Fit(["a", "b", "c", "d"], 2).ShouldBe(["a", "b\nc\nd"]);
        MessageParts.Fit(["a", "b", "c"], 1).ShouldBe(["a\nb\nc"]);
        MessageParts.Fit(["a", "b"], 4).ShouldBe(["a", "b"]);
    }
}
