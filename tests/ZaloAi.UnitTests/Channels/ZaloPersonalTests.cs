using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.UnitTests.Channels;

public sealed class RequestSignatureTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef-test";

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var header = RequestSignature.Create(Secret, Now, "POST", "/internal/zalo-personal/sync", "{\"a\":1}");
        RequestSignature.IsValid(header, Secret, Now.AddSeconds(30), "POST", "/internal/zalo-personal/sync", "{\"a\":1}").ShouldBeTrue();
    }

    [Theory]
    [InlineData("wrong-secret-wrong-secret-wrong-secret", "POST", "/internal/zalo-personal/sync", "{\"a\":1}")]
    [InlineData(Secret, "GET", "/internal/zalo-personal/sync", "{\"a\":1}")]
    [InlineData(Secret, "POST", "/internal/zalo-personal/events", "{\"a\":1}")]
    [InlineData(Secret, "POST", "/internal/zalo-personal/sync", "{\"a\":2}")]
    public void Tampering_with_secret_method_path_or_body_is_rejected(string secret, string method, string path, string body)
    {
        var header = RequestSignature.Create(Secret, Now, "POST", "/internal/zalo-personal/sync", "{\"a\":1}");
        RequestSignature.IsValid(header, secret, Now, method, path, body).ShouldBeFalse();
    }

    [Fact]
    public void Old_or_future_signatures_are_rejected()
    {
        var header = RequestSignature.Create(Secret, Now, "POST", "/x", "");
        RequestSignature.IsValid(header, Secret, Now.AddMinutes(6), "POST", "/x", "").ShouldBeFalse();
        RequestSignature.IsValid(header, Secret, Now.AddMinutes(-6), "POST", "/x", "").ShouldBeFalse();
        RequestSignature.IsValid(header, Secret, Now.AddMinutes(4), "POST", "/x", "").ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=abc,v1=abc")]
    [InlineData("t=1731400000")]
    public void Malformed_headers_are_rejected(string? header) =>
        RequestSignature.IsValid(header, Secret, Now, "POST", "/x", "").ShouldBeFalse();

    [Fact]
    public void Signature_matches_the_contract_format()
    {
        // CONTRACT.md mục 1: v1 = hex(HMAC-SHA256(secret, "t.METHOD.path.body")), sidecar Node tính y hệt.
        var header = RequestSignature.Create("k", DateTimeOffset.FromUnixTimeSeconds(1700000000), "post", "/p?q=1", "b");
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            "k"u8.ToArray(), "1700000000.POST./p?q=1.b"u8.ToArray()));
        header.ShouldBe($"t=1700000000,v1={expected}");
    }
}

public sealed class ZaloPersonalHelpersTests
{
    [Fact]
    public void Bot_typing_delay_grows_with_length_and_stays_within_bounds()
    {
        var options = new ZaloPersonalOptions { ReplyDelayMinMs = 3000, ReplyDelayMaxMs = 12000 };
        for (var i = 0; i < 100; i++)
        {
            var random = new Random(i);
            ZaloPersonalAdapter.TypingDelayMs("ok", options, random).ShouldBeInRange(3000, 12000);
            ZaloPersonalAdapter.TypingDelayMs(new string('a', 5000), options, random).ShouldBe(12000);
        }

        ZaloPersonalAdapter.TypingDelayMs(new string('a', 100), options, new Random(1))
            .ShouldBeGreaterThan(ZaloPersonalAdapter.TypingDelayMs("a", options, new Random(1)));
    }

    [Theory]
    [InlineData("""{"cookie":[{"key":"a","value":"b"}],"imei":"i","userAgent":"u"}""", true)]
    [InlineData("""{"cookie":{"cookies":[]},"imei":"i","userAgent":"u"}""", true)]
    [InlineData("""{"cookie":[],"imei":"","userAgent":"u"}""", false)]
    [InlineData("""{"imei":"i","userAgent":"u"}""", false)]
    [InlineData("""{"cookie":"x","imei":"i","userAgent":"u"}""", false)]
    [InlineData("""[1,2]""", false)]
    public void Session_must_have_cookie_imei_and_user_agent(string json, bool valid)
    {
        using var doc = JsonDocument.Parse(json);
        ZaloPersonalLoginService.IsValidSession(doc.RootElement).ShouldBe(valid);
    }

    [Theory]
    [InlineData("Development", ZaloPersonalMode.Fake, true)]
    [InlineData("Production", ZaloPersonalMode.Fake, false)]
    [InlineData("Staging", ZaloPersonalMode.Fake, false)]
    [InlineData("Production", ZaloPersonalMode.Real, true)]
    public void Fake_mode_only_starts_in_development(string environment, ZaloPersonalMode mode, bool ok)
    {
        var validator = new ZaloPersonalOptionsValidator(new Env(environment));
        validator.Validate(null, new ZaloPersonalOptions { Mode = mode }).Succeeded.ShouldBe(ok);
    }

    [Fact]
    public void Short_shared_secret_is_invalid()
    {
        var results = new ZaloPersonalOptions { SharedSecret = "short" }.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(new object()));
        results.ShouldNotBeEmpty();
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class PersonalPacingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    private static readonly ZaloPersonalOptions Options = new();

    private static ZaloAi.Core.Entities.ZaloPersonalAccount Account(int ageDays) =>
        new() { SessionEnc = "x", CreatedAt = Now.AddDays(-ageDays) };

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(6, 3, 1)]
    [InlineData(7, 5, 2)]
    [InlineData(13, 5, 2)]
    [InlineData(14, 10, null)]
    [InlineData(400, 10, null)]
    public void Safe_mode_ramps_invites_for_new_nicks(int ageDays, int invites, int? week)
    {
        var limits = PersonalPacing.Effective(Account(ageDays), Options, Now);
        limits.InvitesPerDay.ShouldBe(invites);
        limits.NewNickWeek.ShouldBe(week);
        limits.NewContactsPerDay.ShouldBe(PersonalPacing.SafeNewContactsPerDay);
        limits.MessagesPerDay.ShouldBe(PersonalPacing.SafeMessagesPerDay);
    }

    [Fact]
    public void Custom_values_are_used_but_never_exceed_the_system_ceiling()
    {
        var account = Account(30);
        account.PacingMode = ZaloAi.Core.Entities.PacingMode.Custom;
        account.CustomInvitesPerDay = 15;
        account.CustomNewContactsPerDay = 999;
        account.CustomMessagesPerDay = 250;

        var limits = PersonalPacing.Effective(account, Options, Now);

        limits.InvitesPerDay.ShouldBe(15);
        limits.NewContactsPerDay.ShouldBe(Options.MaxNewContactsPerDay);
        limits.MessagesPerDay.ShouldBe(250);
    }

    [Fact]
    public void Custom_mode_still_respects_the_new_nick_ramp()
    {
        var account = Account(2);
        account.PacingMode = ZaloAi.Core.Entities.PacingMode.Custom;
        account.CustomInvitesPerDay = 20;
        PersonalPacing.Effective(account, Options, Now).InvitesPerDay.ShouldBe(3);
    }

    [Theory]
    [InlineData(ZaloAi.Core.Entities.OutreachLevel.Slow, 5, 10)]
    [InlineData(ZaloAi.Core.Entities.OutreachLevel.Paused, 0, 0)]
    public void Brake_halves_or_stops_outreach_but_not_replies(ZaloAi.Core.Entities.OutreachLevel level, int invites, int newContacts)
    {
        var account = Account(30);
        account.OutreachLevel = level;
        var limits = PersonalPacing.Effective(account, Options, Now);
        limits.InvitesPerDay.ShouldBe(invites);
        limits.NewContactsPerDay.ShouldBe(newContacts);
        limits.MessagesPerDay.ShouldBe(PersonalPacing.SafeMessagesPerDay);
    }

    [Fact]
    public void Custom_mode_with_brake_off_ignores_the_brake_level()
    {
        var account = Account(30);
        account.PacingMode = ZaloAi.Core.Entities.PacingMode.Custom;
        account.AutoBrake = false;
        account.OutreachLevel = ZaloAi.Core.Entities.OutreachLevel.Paused;
        PersonalPacing.Effective(account, Options, Now).InvitesPerDay.ShouldBe(PersonalPacing.SafeInvitesPerDay);
    }

    [Fact]
    public void Day_key_uses_vietnam_time()
    {
        // 17:30 UTC ngày 19 = 00:30 ngày 20 giờ Việt Nam.
        PersonalPacing.DayKey(new DateTimeOffset(2026, 11, 19, 17, 30, 0, TimeSpan.Zero)).ShouldBe("20261120");
    }

    [Theory]
    [InlineData("http://user:pass@Proxy.Example.vn:8080", "http://user:pass@proxy.example.vn:8080", "proxy.example.vn:8080")]
    [InlineData("https://u:p@1.2.3.4:443", "https://u:p@1.2.3.4:443", "1.2.3.4:443")]
    public void Proxy_urls_are_normalized(string input, string normalized, string endpoint) =>
        ProxyPoolService.Normalize(input).ShouldBe((normalized, endpoint));

    [Theory]
    [InlineData("socks5://u:p@h:1080")]
    [InlineData("http://u:p@h")]
    [InlineData("http://u:p@h:8080/path")]
    [InlineData("not a url")]
    public void Invalid_proxy_urls_are_rejected(string input) =>
        Should.Throw<ZaloAi.Core.Errors.InvalidInputException>(() => ProxyPoolService.Normalize(input));
}

public sealed class OutreachGreetingTests
{
    [Fact]
    public void Referral_greeting_discloses_ai_names_the_referrer_and_only_asks_permission()
    {
        var text = OutreachGreeting.FirstMessage("Hà", "em", "Spa An Nhiên", "chị", "Hoa", "referral", "chị Lan");
        text.ShouldStartWith("Dạ em chào chị Hoa!");
        text.ShouldContain("trợ lý AI của Spa An Nhiên");
        text.ShouldContain("Chị Lan có giới thiệu chị Hoa với bên em ạ.");
        text.ShouldEndWith("Chị Hoa có muốn em gửi thông tin dịch vụ phù hợp không ạ?");
        text.Any(char.IsDigit).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, null, "Dạ em chào mình!")]
    [InlineData(null, "Hoa", "Dạ em chào Hoa!")]
    [InlineData("anh", null, "Dạ em chào anh!")]
    [InlineData("anh/chị", "Nam", "Dạ em chào Nam!")]
    public void Salutation_never_uses_slash_forms(string? salutation, string? name, string start) =>
        OutreachGreeting.FirstMessage("Hà", "em", "Spa", salutation, name, "inquiry", null).ShouldStartWith(start);

    [Fact]
    public void Invite_greeting_fits_zalo_limit()
    {
        var text = OutreachGreeting.Invite("em", new string('X', 200), "chị", "Hoa", "referral", "chị Lan");
        text.Length.ShouldBeLessThanOrEqualTo(OutreachGreeting.InviteMaxLength);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, OutreachLevel.Normal)] // chưa đủ mẫu
    [InlineData(10, 2, 9, 5, OutreachLevel.Normal)] // 19 < 20 mẫu
    [InlineData(20, 10, 0, 3, OutreachLevel.Slow)] // 15% từ chối
    [InlineData(10, 8, 10, 6, OutreachLevel.Paused)] // 30% từ chối
    [InlineData(25, 4, 0, 0, OutreachLevel.Slow)] // chấp nhận 16% < 20%
    [InlineData(25, 10, 30, 2, OutreachLevel.Normal)]
    public void Brake_levels_follow_the_plan_thresholds(int invites, int accepted, int firstMessages, int negatives, OutreachLevel expected) =>
        ZaloAi.Infrastructure.Jobs.OutreachMaintenanceJob.Decide(invites, accepted, firstMessages, negatives).Level.ShouldBe(expected);
}
