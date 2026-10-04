using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Channels;

public sealed class ZaloClientTests
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

    private static readonly ZaloOptions Options = new()
    {
        AppId = "35296570815181295",
        AppSecret = "app-secret",
        OAuthRedirectUrl = "https://dev-api.example.vn/connect/zalo/callback",
    };

    private static (ZaloClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, string, HttpResponseMessage> respond, ZaloOptions? options = null)
    {
        var handler = new FakeHandler(respond);
        return (new ZaloClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(options ?? Options)), handler);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void Permission_url_matches_console_format()
    {
        var (client, _) = Create((_, _) => Json("{}"));
        client.BuildPermissionUrl("abc+/xyz", "state1").ShouldBe(
            "https://oauth.zaloapp.com/v4/oa/permission?app_id=35296570815181295" +
            "&redirect_uri=https%3A%2F%2Fdev-api.example.vn%2Fconnect%2Fzalo%2Fcallback&code_challenge=abc%2B%2Fxyz&state=state1");
    }

    [Fact]
    public async Task Exchange_code_sends_documented_form_and_secret_header()
    {
        var (client, handler) = Create((_, _) => Json("""{"access_token":"AT","refresh_token":"RT","expires_in":"90000"}"""));

        var tokens = await client.ExchangeCodeAsync("the-code", "the-verifier", CancellationToken.None);

        tokens.ShouldBe(new ZaloTokens("AT", "RT", 90000));
        var (request, body) = handler.Calls.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe("https://oauth.zaloapp.com/v4/oa/access_token");
        request.Headers.GetValues("secret_key").ShouldBe(["app-secret"]);
        request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/x-www-form-urlencoded");
        body.ShouldBe("code=the-code&app_id=35296570815181295&grant_type=authorization_code&code_verifier=the-verifier");
    }

    [Fact]
    public async Task Refresh_sends_refresh_grant()
    {
        var (client, handler) = Create((_, _) => Json("""{"access_token":"AT2","refresh_token":"RT2","expires_in":90000}"""));

        (await client.RefreshAsync("RT", CancellationToken.None)).RefreshToken.ShouldBe("RT2");
        handler.Calls.ShouldHaveSingleItem().Body.ShouldBe("refresh_token=RT&app_id=35296570815181295&grant_type=refresh_token");
    }

    [Fact]
    public async Task Token_endpoint_without_tokens_throws_without_leaking_body()
    {
        var (client, _) = Create((_, _) => Json("""{"error":-14014,"error_name":"Invalid refresh token","secret":"RT-should-not-leak"}"""));

        var ex = await Should.ThrowAsync<ZaloApiException>(() => client.RefreshAsync("RT", CancellationToken.None));
        ex.Message.ShouldNotContain("RT-should-not-leak");
        ex.Code.ShouldBe(-14014);
    }

    [Fact]
    public async Task Send_text_posts_documented_body_with_token_header()
    {
        var (client, handler) = Create((_, _) => Json("""
            {"data":{"quota":{"quota_type":"reply","remain":"8","total":"8"},"message_id":"63ecf43f0df7dba892e6","user_id":"2512523625412515","sent_time":"1626926349402"},"error":0,"message":"Success"}
            """));

        var result = await client.SendTextAsync("AT", "2512523625412515", "hello, world!", CancellationToken.None);

        result.ShouldBe(new ZaloSendResult("63ecf43f0df7dba892e6", "reply"));
        var (request, body) = handler.Calls.ShouldHaveSingleItem();
        request.RequestUri!.ToString().ShouldBe("https://openapi.zalo.me/v3.0/oa/message/cs");
        request.Headers.GetValues("access_token").ShouldBe(["AT"]);
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("recipient").GetProperty("user_id").GetString().ShouldBe("2512523625412515");
        json.GetProperty("message").GetProperty("text").GetString().ShouldBe("hello, world!");
    }

    [Theory]
    [InlineData(-216, true, false, false)]
    [InlineData(-220, true, false, false)]
    [InlineData(-223, false, true, false)]
    [InlineData(-230, false, false, true)]
    [InlineData(-232, false, false, true)]
    public async Task Api_errors_are_classified(int code, bool token, bool authorization, bool recipient)
    {
        var (client, _) = Create((_, _) => Json($$"""{"error":{{code}},"message":"x"}"""));

        var ex = await Should.ThrowAsync<ZaloApiException>(() => client.SendTextAsync("AT", "u", "hi", CancellationToken.None));
        ex.IsTokenError.ShouldBe(token);
        ex.IsAuthorizationError.ShouldBe(authorization);
        ex.IsRecipientError.ShouldBe(recipient);
        ex.ShortCode.ShouldBe($"zalo:{code}");
    }

    [Fact]
    public async Task Rate_limit_and_server_errors_are_transient()
    {
        var (limited, _) = Create((_, _) => Json("""{"error":-32,"message":"Your OA reached limit call api"}"""));
        await Should.ThrowAsync<ZaloTransientException>(() => limited.SendTextAsync("AT", "u", "hi", CancellationToken.None));

        var (down, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await Should.ThrowAsync<ZaloTransientException>(() => down.SendTextAsync("AT", "u", "hi", CancellationToken.None));
    }

    [Fact]
    public async Task Not_configured_is_reported()
    {
        var (client, _) = Create((_, _) => Json("{}"), new ZaloOptions());
        Should.Throw<ZaloNotConfiguredException>(() => client.BuildPermissionUrl("c", "s"));
        await Should.ThrowAsync<ZaloNotConfiguredException>(() => client.RefreshAsync("RT", CancellationToken.None));
    }

    [Fact]
    public void Split_text_keeps_parts_within_limit_and_prefers_line_breaks()
    {
        var text = string.Join("\n", Enumerable.Repeat(new string('a', 900), 5));

        var parts = ZaloClient.SplitText(text);

        parts.ShouldAllBe(p => p.Length <= ZaloClient.MaxTextLength);
        string.Concat(parts).Replace("\n", "", StringComparison.Ordinal).Length.ShouldBe(4500);
        parts[0].ShouldBe(string.Join("\n", Enumerable.Repeat(new string('a', 900), 2)));
        ZaloClient.SplitText("ngắn").ShouldBe(["ngắn"]);
    }
}

public sealed class ZaloSecurityTests
{
    [Fact]
    public void Pkce_verifier_and_challenge_follow_docs()
    {
        var verifier = ZaloPkce.NewVerifier();
        verifier.Length.ShouldBe(43);
        verifier.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c));

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=');
        ZaloPkce.Challenge(verifier).ShouldBe(expected);
        ZaloPkce.Challenge(verifier).ShouldNotEndWith("=");
        ZaloPkce.NewState().ShouldNotBe(ZaloPkce.NewState());
    }

    [Fact]
    public void Signature_is_sha256_of_appid_body_timestamp_secret()
    {
        const string body = """{"event_name":"user_send_text"}""";
        var mac = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("app" + body + "123" + "secret"))).ToLowerInvariant();

        ZaloWebhookSignature.IsValid(mac, "app", body, "123", "secret").ShouldBeTrue();
        ZaloWebhookSignature.IsValid("mac=" + mac, "app", body, "123", "secret").ShouldBeTrue();
        ZaloWebhookSignature.IsValid(mac.ToUpperInvariant(), "app", body, "123", "secret").ShouldBeTrue();
        ZaloWebhookSignature.IsValid(mac, "app", body + " ", "123", "secret").ShouldBeFalse();
        ZaloWebhookSignature.IsValid(mac, "app", body, "124", "secret").ShouldBeFalse();
        ZaloWebhookSignature.IsValid(mac, "app", body, "123", "other").ShouldBeFalse();
        ZaloWebhookSignature.IsValid(null, "app", body, "123", "secret").ShouldBeFalse();
        ZaloWebhookSignature.IsValid(mac, "app", body, "123", "").ShouldBeFalse();
        ZaloWebhookSignature.DescribeFormat("mac=" + mac).ShouldBe("prefix='mac=' length=64 hex=True case=lower");
    }
}

public sealed class ZaloWebhookEventTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Zalo", name));

    [Fact]
    public void Parses_user_text_event_from_docs()
    {
        var e = ZaloWebhookEvent.Parse(Fixture("user_send_text.json")).ShouldNotBeNull();

        e.IsUserMessage.ShouldBeTrue();
        e.OaId.ShouldBe("388613280878808645");
        e.UserId.ShouldBe("246845883529197922");
        e.MessageId.ShouldBe("96d3cdf3af150460909");
        e.Text.ShouldBe("message");
        e.Timestamp.ShouldBe("154390853474");
        e.Attachments.ShouldBeEmpty();
    }

    [Fact]
    public void Parses_image_attachments()
    {
        var e = ZaloWebhookEvent.Parse(Fixture("user_send_image.json")).ShouldNotBeNull();

        e.Text.ShouldBeNull();
        e.Attachments.ShouldHaveSingleItem().ShouldBe(new ZaloAttachment("image", "http://f6.photo.talk.zdn.vn/9c2db3ccc0a223fc7ab3.jpg"));
    }

    [Fact]
    public void Parses_oa_send_event_with_admin()
    {
        var e = ZaloWebhookEvent.Parse(Fixture("oa_send_text_admin.json")).ShouldNotBeNull();

        e.IsOaMessage.ShouldBeTrue();
        e.OaId.ShouldBe("388613280879808645");
        e.UserId.ShouldBe("246845883529197922");
        e.SenderAdminId.ShouldBe("4267886274574868951");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"event_name":"user_send_text"}""")]
    public void Invalid_payload_is_null(string body) => ZaloWebhookEvent.Parse(body).ShouldBeNull();
}
