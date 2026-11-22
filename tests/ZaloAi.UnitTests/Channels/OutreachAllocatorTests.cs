using Shouldly;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.UnitTests.Channels;

/// <summary>Chia danh sách SĐT cho nhiều nick (chủ dự án chốt 2026-11-15): theo sức gửi, khách quen giữ nick cũ.</summary>
public sealed class OutreachAllocatorTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static List<Guid?> People(int n) => Enumerable.Repeat<Guid?>(null, n).ToList();

    [Fact]
    public void Stronger_nick_gets_more_so_both_finish_together()
    {
        // A nhận 20 người mới/ngày, B 10: 90 số → A 60, B 30, cùng xong sau 3 ngày (chia đều 45/45 thì B mất 5 ngày).
        var (assignments, summary) = OutreachAllocator.Allocate(People(90), [new(A, 20, 0), new(B, 10, 0)]);

        assignments.Count(x => x == A).ShouldBe(60);
        assignments.Count(x => x == B).ShouldBe(30);
        summary.Single(s => s.ConnectionId == A).EtaDays.ShouldBe(3);
        summary.Single(s => s.ConnectionId == B).EtaDays.ShouldBe(3);
    }

    [Fact]
    public void Existing_queue_is_counted_before_new_people()
    {
        // A đang có 30 người chờ, B trống, cùng 10/ngày: 10 số mới → B nhận hết.
        var (assignments, summary) = OutreachAllocator.Allocate(People(10), [new(A, 10, 30), new(B, 10, 0)]);

        assignments.ShouldAllBe(x => x == B);
        summary.Single(s => s.ConnectionId == A).QueuedAfter.ShouldBe(30);
        summary.Single(s => s.ConnectionId == B).Assigned.ShouldBe(10);
    }

    [Fact]
    public void Equal_nicks_alternate_evenly()
    {
        var (assignments, _) = OutreachAllocator.Allocate(People(7), [new(A, 10, 0), new(B, 10, 0), new(C, 10, 0)]);

        assignments.GroupBy(x => x).Select(g => g.Count()).OrderBy(n => n).ShouldBe([2, 2, 3]);
    }

    [Fact]
    public void Familiar_people_stay_with_their_nick_if_it_is_still_available()
    {
        var gone = Guid.NewGuid(); // nick cũ không còn trong danh sách được chia → chia như người mới
        var (assignments, _) = OutreachAllocator.Allocate([B, null, gone, B], [new(A, 10, 0), new(B, 10, 0)]);

        assignments[0].ShouldBe(B);
        assignments[3].ShouldBe(B);
        assignments[1].ShouldBe(A); // B đã nhận 2 người quen → người mới sang A
        assignments[2].ShouldBe(A);
    }

    [Fact]
    public void Zero_capacity_counts_as_one_per_day_and_empty_list_is_fine()
    {
        var (assignments, summary) = OutreachAllocator.Allocate(People(0), [new(A, 0, 3)]);

        assignments.ShouldBeEmpty();
        summary.ShouldHaveSingleItem().EtaDays.ShouldBe(3);
        Should.Throw<ArgumentException>(() => OutreachAllocator.Allocate(People(1), []));
    }
}
