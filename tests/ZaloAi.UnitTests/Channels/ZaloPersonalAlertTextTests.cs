using Shouldly;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.UnitTests.Channels;

/// <summary>Tin Telegram về nick Zalo cá nhân (2026-11-17): hết / sắp hết danh sách, phanh, sự cố — chỉ tên nick + con số, link đúng trang.</summary>
public sealed class ZaloPersonalAlertTextTests
{
    private static readonly Guid Nick = Guid.Parse("0199a000-0000-7000-8000-0000000000aa");

    private static string Build(string reason, NickQueueStats? stats = null) =>
        ZaloPersonalAlertText.Build(reason, "Remi Hau", "Khoa Học Huyệt Đạo", "https://app.remi.vn/", Nick, stats);

    [Fact]
    public void Empty_queue_summarises_results_and_links_to_add_numbers()
    {
        var text = Build(OutreachQueueWatcher.QueueEmpty, new NickQueueStats(0, 0, Contacted: 50, Replied: 12, Booked: 3, NoZalo: 4));

        text.ShouldStartWith("📋 Nick Zalo cá nhân \"Remi Hau\" đã nhắn hết danh sách — Khoa Học Huyệt Đạo");
        text.ShouldContain("Đã liên hệ 50 người: 12 trả lời, 3 có lịch hẹn, 4 không có Zalo.");
        text.ShouldEndWith("https://app.remi.vn/zalo-personal?tab=add");
    }

    [Fact]
    public void Low_queue_says_how_many_are_left_and_when_it_runs_out()
    {
        var text = Build(OutreachQueueWatcher.QueueLow, new NickQueueStats(Waiting: 8, EtaDays: 1, 30, 5, 1, 2));

        text.ShouldContain("sắp hết danh sách: còn 8 người chờ, khoảng 1 ngày nữa là nhắn hết");
        text.ShouldContain("/zalo-personal?tab=add");
    }

    [Theory]
    [InlineData("zp:outreach_normal", "✅")]
    [InlineData("zp:outreach_slow", "🐢")]
    [InlineData("zp:outreach_paused", "DỪNG nhắn trước 72 giờ")]
    [InlineData("zp:session_expired", "Vào trang Zalo cá nhân, quét lại mã QR")]
    public void Nick_alerts_link_to_the_nick_page_not_the_old_channels_page(string reason, string expected)
    {
        var text = Build(reason);

        text.ShouldContain(expected);
        text.ShouldContain($"https://app.remi.vn/zalo-personal/{Nick}");
        text.ShouldNotContain("/channels");
    }
}
