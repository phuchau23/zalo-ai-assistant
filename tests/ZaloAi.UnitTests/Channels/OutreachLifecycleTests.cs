using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.UnitTests.Channels;

/// <summary>Vòng đời số điện thoại chăm sóc qua nick (chủ dự án chốt 2026-11-25): chặn dán lại, dành lượt tin cho khách đang chat.</summary>
public sealed class OutreachLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Never_contacted_numbers_can_be_pasted()
    {
        OutreachLifecycle.PasteBlock(0, null, Now, out var retry).ShouldBeNull();
        retry.ShouldBeNull();
    }

    [Fact]
    public void Contacted_once_is_blocked_until_90_days_after_the_last_touch()
    {
        var last = Now.AddDays(-10);
        OutreachLifecycle.PasteBlock(1, last, Now, out var retry).ShouldBe(OutreachLifecycle.ContactedRecently);
        retry.ShouldBe(last.AddDays(90));

        OutreachLifecycle.PasteBlock(1, Now.AddDays(-91), Now, out retry).ShouldBeNull(); // được thử thêm một lần cuối
        retry.ShouldBeNull();
    }

    [Fact]
    public void Contacted_twice_is_blocked_for_good()
    {
        OutreachLifecycle.PasteBlock(2, Now.AddDays(-400), Now, out var retry).ShouldBe(OutreachLifecycle.ContactedTwice);
        retry.ShouldBeNull();
    }

    [Theory]
    [InlineData(200, 140)]
    [InlineData(60, 42)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void New_people_get_seventy_percent_of_the_daily_messages(int perDay, int expected) =>
        OutreachLifecycle.NewOutreachMessageCap(perDay).ShouldBe(expected);

    [Fact]
    public void Call_list_puts_cold_customers_first_and_revival_covers_every_stopped_state()
    {
        OutreachLifecycle.CallStates[0].ShouldBe(OutreachState.Cold);
        OutreachLifecycle.RevivableStates.ShouldBe([OutreachState.NoReply, OutreachState.Cold, OutreachState.Archived, OutreachState.Expired], ignoreOrder: true);
        OutreachLifecycle.ConvertibleStates.ShouldNotContain(OutreachState.Declined);
        OutreachLifecycle.ConvertibleStates.ShouldNotContain(OutreachState.Stopped);
    }
}
