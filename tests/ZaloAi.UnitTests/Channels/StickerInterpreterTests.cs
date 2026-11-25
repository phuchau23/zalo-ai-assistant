using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Infrastructure.Stickers;
using ZaloAi.UnitTests.Ai;

namespace ZaloAi.UnitTests.Channels;

/// <summary>Hiểu sticker (2026-11-25): lưu nhớ theo sticker (Cache-Aside), AI lỗi thì dùng chữ khóa, chỉ tải ảnh https công khai.</summary>
public sealed class StickerInterpreterTests
{
    private sealed class MemoryStore : IDistributedStore
    {
        public Dictionary<string, (string Value, TimeSpan Ttl)> Items { get; } = [];

        public Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan ttl, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryAdd(key, (value, ttl)));

        public Task<string?> TakeAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Items.Remove(key, out var v) ? v.Value : null);

        public Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(key, out var v) ? v.Value : null);

        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
        {
            Items[key] = (value, ttl);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingVision(Func<string?, StickerVisionResult?> answer) : IStickerVision
    {
        public List<(int Bytes, string MimeType, string? Keyword)> Calls { get; } = [];

        public Task<StickerVisionResult?> DescribeAsync(byte[] image, string mimeType, string? keyword, CancellationToken cancellationToken)
        {
            Calls.Add((image.Length, mimeType, keyword));
            return Task.FromResult(answer(keyword));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly BotUsage Usage = new(UsageKind.Chat, "gemini", "m", 300, 10, 0.0001m);

    private static HttpResponseMessage Png(int size = 100) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[size]) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } },
    };

    private static (StickerInterpreter Interpreter, MemoryStore Store, CountingVision Vision, FakeAiHandler Http) Create(
        Func<string?, StickerVisionResult?>? answer = null, Func<HttpResponseMessage>? image = null)
    {
        var store = new MemoryStore();
        var vision = new CountingVision(answer ?? (_ => new StickerVisionResult("giơ ngón cái – đồng ý, ok", Usage)));
        var http = new FakeAiHandler((_, _) => (image ?? (() => Png()))());
        return (new StickerInterpreter(store, vision, new Factory(http), NullLogger<StickerInterpreter>.Instance), store, vision, http);
    }

    [Fact]
    public async Task Same_sticker_is_described_by_ai_once_then_read_from_memory()
    {
        var (interpreter, store, vision, http) = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var info = new StickerInfo("123", "ok", "https://zalo-api.zadn.vn/sticker/123.png");
        await interpreter.RememberAsync(first, info, CancellationToken.None);
        await interpreter.RememberAsync(second, info, CancellationToken.None);

        var a = await interpreter.InterpretAsync(first, CancellationToken.None);
        var b = await interpreter.InterpretAsync(second, CancellationToken.None);

        a.ShouldNotBeNull().Meaning.ShouldBe("giơ ngón cái – đồng ý, ok");
        a.Usage.ShouldBe(Usage); // lần đầu tốn tiền AI → ghi chi phí
        b.ShouldNotBeNull().Meaning.ShouldBe(a.Meaning);
        b.Usage.ShouldBeNull(); // lần sau lấy từ bộ nhớ, không tốn tiền
        vision.Calls.ShouldHaveSingleItem().Keyword.ShouldBe("ok"); // chữ khóa của Zalo đi kèm làm gợi ý
        http.Calls.Count.ShouldBe(1);
        store.Items["sticker:meaning:123"].Ttl.ShouldBe(TimeSpan.FromDays(180));
    }

    [Fact]
    public async Task Ai_failure_falls_back_to_the_zalo_keyword_and_is_retried_later()
    {
        var (interpreter, store, _, _) = Create(_ => throw new AiProviderException("down"));
        var id = Guid.NewGuid();
        await interpreter.RememberAsync(id, new StickerInfo("7", "cảm ơn", "https://zalo-api.zadn.vn/7.png"), CancellationToken.None);

        var meaning = await interpreter.InterpretAsync(id, CancellationToken.None);

        meaning.ShouldNotBeNull().Meaning.ShouldBe("sticker “cảm ơn”");
        meaning.Usage.ShouldBeNull();
        store.Items["sticker:meaning:7"].Ttl.ShouldBe(TimeSpan.FromDays(1)); // chỉ nhớ ngắn: lần sau thử lại AI
    }

    [Fact]
    public async Task Unknown_message_or_nothing_to_go_on_gives_no_meaning()
    {
        var (interpreter, _, vision, _) = Create(_ => null);
        (await interpreter.InterpretAsync(Guid.NewGuid(), CancellationToken.None)).ShouldBeNull(); // không có thông tin sticker

        var id = Guid.NewGuid();
        await interpreter.RememberAsync(id, new StickerInfo("9", null, "http://insecure.example/9.png"), CancellationToken.None);
        (await interpreter.InterpretAsync(id, CancellationToken.None)).ShouldBeNull();
        vision.Calls.ShouldBeEmpty(); // http (không https) → không tải ảnh
    }

    [Fact]
    public async Task Oversized_or_non_image_downloads_are_refused()
    {
        var (big, _, bigVision, _) = Create(image: () => Png(StickerInterpreter.MaxImageBytes + 1));
        var id = Guid.NewGuid();
        await big.RememberAsync(id, new StickerInfo("5", "haha", "https://zalo-api.zadn.vn/5.png"), CancellationToken.None);
        (await big.InterpretAsync(id, CancellationToken.None)).ShouldNotBeNull().Meaning.ShouldBe("sticker “haha”");
        bigVision.Calls.ShouldBeEmpty();

        var (html, _, htmlVision, _) = Create(image: () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });
        var other = Guid.NewGuid();
        await html.RememberAsync(other, new StickerInfo("6", null, "https://zalo-api.zadn.vn/6.png"), CancellationToken.None);
        (await html.InterpretAsync(other, CancellationToken.None)).ShouldBeNull();
        htmlVision.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://zalo-api.zadn.vn/api/emoticon/sticker/webpc?eid=1", true)]
    [InlineData("http://zalo-api.zadn.vn/a.png", false)]
    [InlineData("https://127.0.0.1/a.png", false)]
    [InlineData("https://[::1]/a.png", false)]
    [InlineData("https://localhost/a.png", false)]
    [InlineData("https://intranet/a.png", false)]
    [InlineData("file:///c:/secret.png", false)]
    [InlineData(null, false)]
    public void Only_public_https_image_urls_are_fetched(string? url, bool allowed) =>
        (StickerInterpreter.SafeImageUri(url) is not null).ShouldBe(allowed);

    [Fact]
    public void Cache_key_uses_the_sticker_id_or_a_hash_of_the_image_url()
    {
        StickerInterpreter.CacheKey(new StickerInfo("42", null, "https://a.b/c.png")).ShouldBe("sticker:meaning:42");
        var byUrl = StickerInterpreter.CacheKey(new StickerInfo(null, null, "https://a.b/c.png"));
        byUrl.ShouldNotBeNull().ShouldStartWith("sticker:meaning:u:");
        byUrl.ShouldNotContain("a.b"); // không lộ đường dẫn trong khóa
        StickerInterpreter.CacheKey(new StickerInfo(null, "ok", null)).ShouldBeNull();
    }

    [Fact]
    public async Task Gemini_gets_the_image_inline_and_the_answer_is_cleaned()
    {
        var handler = new FakeAiHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                candidates = new[] { new { content = new { parts = new[] { new { text = "\"Giơ ngón cái – đồng ý, ok.\"\nGiải thích thêm" } } } } },
                usageMetadata = new { promptTokenCount = 300, candidatesTokenCount = 8 },
            })),
        });
        var vision = new GeminiStickerVision(new HttpClient(handler) { BaseAddress = new Uri("https://gemini.test/v1beta/") }, Microsoft.Extensions.Options.Options.Create(AiFakes.Options()));

        var result = await vision.DescribeAsync([1, 2, 3], "image/webp", "ok", CancellationToken.None);

        result.ShouldNotBeNull().Meaning.ShouldBe("Giơ ngón cái – đồng ý, ok");
        result.Usage.Provider.ShouldBe("gemini");
        var (request, body) = handler.Calls.ShouldHaveSingleItem();
        request.RequestUri!.AbsolutePath.ShouldEndWith($"models/{AiFakes.GeminiMain}:generateContent");
        var part = JsonDocument.Parse(body).RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inlineData");
        part.GetProperty("mimeType").GetString().ShouldBe("image/webp");
        part.GetProperty("data").GetString().ShouldBe(Convert.ToBase64String(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("  ", null)]
    [InlineData("**Vẫy tay – chào**", "Vẫy tay – chào")]
    public void Clean_keeps_one_short_line(string text, string? expected) => GeminiStickerVision.Clean(text).ShouldBe(expected);
}
