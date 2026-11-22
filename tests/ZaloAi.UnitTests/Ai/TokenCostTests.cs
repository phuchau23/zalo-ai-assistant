using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Bot;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.UnitTests.Ai;

/// <summary>Giảm chi phí token (chủ dự án duyệt 2026-11-15): prompt "cố định trước", tin xã giao không tìm dữ liệu, tính giá phần cache.</summary>
public sealed class TokenCostTests
{
    private static readonly BotProfile SpaA = new("Khoa Học Huyệt Đạo", "spa", "Huyệt Đạo", "em", BotTone.Human, null, null);
    private static readonly BotProfile SpaB = new("Spa An Nhiên", "spa", "Mai", "em", BotTone.Human, null, null);

    private static readonly KnowledgeHit Massage = new(Guid.NewGuid(), "item", Guid.NewGuid(), "DV-MS60", "Massage body 60 phút", "Massage body 60 phút: 450.000đ.", 0.8, false);

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    [Fact]
    public void Turn_specific_parts_come_after_the_shared_prefix()
    {
        var spa = IndustryTemplateRegistry.Get("spa");
        var first = PromptBuilder.BuildSystem(
            spa, SpaA, [new PromptChunk("C1", Massage)], null, new DateTimeOffset(2026, 11, 15, 1, 0, 0, TimeSpan.Zero), Addressing.From(null, false));
        var later = PromptBuilder.BuildSystem(
            spa, SpaA, [], "- Khách hỏi giá", new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero), Addressing.From("chị", true), maxMessages: 2);
        var otherBusiness = PromptBuilder.BuildSystem(
            spa, SpaB, [], null, new DateTimeOffset(2026, 11, 15, 1, 0, 0, TimeSpan.Zero), Addressing.From(null, false));

        // Cùng DN: mọi thứ trước "LƯỢT NÀY" giống hệt dù giờ, cách gọi khách, dữ liệu khác nhau → nhà cung cấp AI cache được phần này.
        var turnStart = first.IndexOf("## LƯỢT NÀY", StringComparison.Ordinal);
        turnStart.ShouldBeGreaterThan(0);
        CommonPrefix(first, later).ShouldBeGreaterThanOrEqualTo(turnStart);
        first.IndexOf("Thời gian hiện tại", StringComparison.Ordinal).ShouldBeGreaterThan(turnStart);
        first.IndexOf("## DỮ LIỆU THAM KHẢO", StringComparison.Ordinal).ShouldBeGreaterThan(turnStart);

        // Khác DN cùng ngành: quy tắc hệ thống + vai trò ngành giống hệt; tên DN chỉ xuất hiện từ mục DOANH NGHIỆP.
        var businessStart = first.IndexOf("## DOANH NGHIỆP", StringComparison.Ordinal);
        CommonPrefix(first, otherBusiness).ShouldBeGreaterThanOrEqualTo(businessStart);
        first[..businessStart].ShouldNotContain("Khoa Học Huyệt Đạo");
        first[businessStart..turnStart].ShouldContain("Bạn tên Huyệt Đạo, trợ lý AI chăm sóc khách hàng của \"Khoa Học Huyệt Đạo\"");
    }

    [Theory]
    [InlineData("Dạ em cảm ơn nhiều nha", true)]
    [InlineData("ok cám ơn shop", true)]
    [InlineData("Thanks em", true)]
    [InlineData("tks ad 🥰", true)]
    [InlineData("bye em nhé", true)]
    [InlineData("Dạ vâng, hẹn gặp lại ạ", true)]
    [InlineData("ok", false)] // đồng ý điều bot vừa đề nghị → cần dữ liệu (quy tắc 11b)
    [InlineData("có", false)]
    [InlineData("👍", false)]
    [InlineData("cảm ơn, gói 10 buổi giá bao nhiêu vậy", false)]
    [InlineData("cảm ơn em, mai chị qua", false)]
    [InlineData("không cần đâu cảm ơn", false)] // từ chối → luồng bình thường (dừng chăm sóc)
    [InlineData("", false)]
    public void Small_talk_is_only_a_whole_thanks_or_goodbye_message(string text, bool expected) =>
        SmallTalk.IsClosing(text).ShouldBe(expected);

    [Fact]
    public void Cost_prices_cached_input_at_the_cached_rate()
    {
        var options = new AiOptions
        {
            Pricing = new(StringComparer.OrdinalIgnoreCase)
            {
                ["lite"] = new ModelPrice { InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 2.50m, CachedInputPerMillionUsd = 0.03m },
                ["no-cache-price"] = new ModelPrice { InputPerMillionUsd = 0.30m, OutputPerMillionUsd = 2.50m },
            },
        };

        options.Cost("lite", 1_000_000, 0).ShouldBe(0.30m);
        options.Cost("lite", 1_000_000, 0, cachedInputTokens: 800_000).ShouldBe(0.084m); // 200k × 0,30 + 800k × 0,03
        options.Cost("lite", 100, 0, cachedInputTokens: 500).ShouldBe(0.000003m); // cache > tổng (lỗi số liệu) → kẹp lại
        options.Cost("no-cache-price", 1_000_000, 0, cachedInputTokens: 800_000).ShouldBe(0.30m);
    }

    [Fact]
    public async Task Gemini_reports_cached_tokens_and_cheaper_cost()
    {
        var body = new
        {
            candidates = new[] { new { content = new { role = "model", parts = new object[] { new { text = "{}" } } }, finishReason = "STOP" } },
            usageMetadata = new { promptTokenCount = 5000, candidatesTokenCount = 100, cachedContentTokenCount = 4096 },
        };
        var handler = new FakeAiHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
        var options = AiFakes.Options(o => o.Pricing[AiFakes.GeminiMain] = new ModelPrice { InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m, CachedInputPerMillionUsd = 0.1m });

        var result = await AiFakes.Gemini(handler, options).CallAsync(AiFakes.GeminiMain, AiFakes.Request(), CancellationToken.None);

        result.InputTokens.ShouldBe(5000);
        result.CachedInputTokens.ShouldBe(4096);
        result.CostUsd.ShouldBe(((904 * 1m) + (4096 * 0.1m) + (100 * 2m)) / 1_000_000m);
    }

    [Fact]
    public async Task Groq_reports_cached_tokens_from_prompt_token_details()
    {
        var body = new
        {
            id = "chatcmpl-1",
            model = AiFakes.GroqMain,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "{\"reply\":\"ok\"}" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 2000, completion_tokens = 50, prompt_tokens_details = new { cached_tokens = 1500 } },
        };
        var handler = new FakeAiHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
        var options = AiFakes.Options(o => o.Pricing[AiFakes.GroqMain] = new ModelPrice { InputPerMillionUsd = 0.15m, OutputPerMillionUsd = 0.60m, CachedInputPerMillionUsd = 0.075m });

        var result = await AiFakes.Groq(handler, options).CallAsync(AiFakes.GroqMain, AiFakes.Request(), CancellationToken.None);

        result.CachedInputTokens.ShouldBe(1500);
        result.CostUsd.ShouldBe(((500 * 0.15m) + (1500 * 0.075m) + (50 * 0.60m)) / 1_000_000m);
    }
}
