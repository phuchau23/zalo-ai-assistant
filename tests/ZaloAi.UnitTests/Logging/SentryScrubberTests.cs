using Sentry;
using Shouldly;
using ZaloAi.Infrastructure.Logging;

namespace ZaloAi.UnitTests.Logging;

public sealed class SentryScrubberTests
{
    [Fact]
    public void Exception_message_phone_and_email_are_masked()
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("Zalo trả lỗi cho khách 0912345678 / khach@example.com"));
        sentryEvent.SentryExceptions = [new Sentry.Protocol.SentryException { Value = "Zalo trả lỗi cho khách 0912345678 / khach@example.com" }];

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        var value = scrubbed.SentryExceptions.ShouldNotBeNull().ShouldHaveSingleItem().Value.ShouldNotBeNull();
        value.ShouldNotContain("0912345678");
        value.ShouldNotContain("khach@example.com");
        value.ShouldContain("Zalo trả lỗi cho khách");
    }

    [Fact]
    public void Log_message_is_masked()
    {
        var sentryEvent = new SentryEvent { Message = new SentryMessage { Message = "gọi lại 0987 654 321", Formatted = "gọi lại 0987 654 321" } };

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        scrubbed.Message!.Formatted.ShouldBe("gọi lại ***");
        scrubbed.Message.Message.ShouldBe("gọi lại ***");
    }

    [Fact]
    public void Request_query_string_cookies_headers_and_body_are_removed()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.Request.Url = "https://api.example.com/connect/zalo/callback";
        sentryEvent.Request.QueryString = "code=SECRET-OAUTH-CODE&state=abc";
        sentryEvent.Request.Cookies = "zaloai.session=abc";
        sentryEvent.Request.Data = "{\"password\":\"x\"}";
        sentryEvent.Request.Headers["Authorization"] = "Bearer x";

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        scrubbed.Request.Url.ShouldBe("https://api.example.com/connect/zalo/callback");
        scrubbed.Request.QueryString.ShouldBeNull();
        scrubbed.Request.Cookies.ShouldBeNull();
        scrubbed.Request.Data.ShouldBeNull();
        scrubbed.Request.Headers.ShouldBeEmpty();
    }

    [Fact]
    public void Breadcrumb_message_is_masked_and_data_dropped()
    {
        var breadcrumb = new Breadcrumb(
            "Khách 0912345678 hỏi giá",
            "default",
            new Dictionary<string, string> { ["url"] = "/x?code=secret" },
            "app");

        var scrubbed = SentryScrubber.Scrub(breadcrumb).ShouldNotBeNull();

        scrubbed.Message.ShouldBe("Khách *** hỏi giá");
        scrubbed.Data.ShouldBeNull();
        scrubbed.Category.ShouldBe("app");
    }
}
