using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Inbox;

namespace ZaloAi.UnitTests.Channels;

public sealed class TelegramClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static readonly TelegramOptions Options = new() { BotToken = "8250209780:AAF-test_token" };

    [Fact]
    public void Token_with_colon_builds_https_bot_url()
    {
        var uri = TelegramClient.SendMessageUri(Options);
        uri.Scheme.ShouldBe("https");
        uri.ToString().ShouldBe("https://api.telegram.org/bot8250209780:AAF-test_token/sendMessage");
    }

    [Fact]
    public async Task Sends_to_bot_api_and_reports_success()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new TelegramClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<TelegramClient>.Instance);

        (await client.SendAsync("-100123", "xin chào", CancellationToken.None)).ShouldBeTrue();
        handler.Requests.ShouldHaveSingleItem().RequestUri!.AbsolutePath.ShouldEndWith("/sendMessage");
    }

    [Fact]
    public async Task Failures_never_throw()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("boom"));
        var client = new TelegramClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options), NullLogger<TelegramClient>.Instance);

        (await client.SendAsync("-100123", "x", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Not_configured_or_no_chat_does_nothing()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new TelegramClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new TelegramOptions()), NullLogger<TelegramClient>.Instance);

        (await client.SendAsync("-100123", "x", CancellationToken.None)).ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }
}
