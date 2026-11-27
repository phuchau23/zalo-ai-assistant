using Shouldly;
using ZaloAi.Infrastructure.Jobs;

namespace ZaloAi.UnitTests.Channels;

/// <summary>Tin Telegram khi khách trong danh sách "Cần gọi điện" tự nhắn lại (chủ dự án chốt 2026-11-27).</summary>
public sealed class LateReplyTextTests
{
    private static readonly Guid Conversation = Guid.Parse("0196f000-0000-7000-8000-000000000001");

    [Fact]
    public void Quiet_customer_reply_names_the_customer_masks_the_phone_and_links_the_chat()
    {
        LateReplyText.Build("Chị Lan", "090***451", new DateTimeOffset(2026, 11, 25, 2, 0, 0, TimeSpan.Zero), wasCold: false, "Spa An", "https://app.remi.vn/", Conversation)
            .ShouldBe(
                "📩 Chị Lan (090***451) — bot nhắn chào 25/11, hôm nay mới trả lời — Spa An\n" +
                "Đã rời danh sách Cần gọi điện, bot đang tư vấn (không cần gọi nữa).\n" +
                $"Mở hội thoại: https://app.remi.vn/inbox?c={Conversation}");
    }

    [Fact]
    public void Cold_customer_and_missing_details_still_read_well()
    {
        var text = LateReplyText.Build(null, null, null, wasCold: true, "Spa An", "https://app.remi.vn", null);
        text.ShouldStartWith("📩 Một khách — từng tư vấn rồi im, hôm nay nhắn lại — Spa An");
        text.ShouldEndWith("https://app.remi.vn/care#call-list");
        LateReplyText.Build("  ", "090***451", null, wasCold: false, "Spa An", "https://x", null).ShouldStartWith("📩 090***451 — hôm nay mới trả lời");
    }
}
