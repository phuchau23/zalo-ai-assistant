using Shouldly;
using ZaloAi.Infrastructure.Crm;

namespace ZaloAi.UnitTests.Crm;

public sealed class SlotCalculatorTests
{
    private static readonly Guid Lan = Guid.NewGuid();
    private static readonly Guid Mai = Guid.NewGuid();

    private static SlotCalculator.Input Input(
        IReadOnlyList<SlotCalculator.StaffAvailability> staff,
        IReadOnlyList<BusyInterval>? branchBusy = null,
        int? capacity = null,
        int duration = 60,
        int buffer = 0,
        int step = 30,
        int earliest = 0,
        IReadOnlyList<(int, int)>? open = null) =>
        new(open ?? [(8 * 60, 12 * 60)], staff, branchBusy ?? [], capacity, duration, buffer, step, earliest);

    [Fact]
    public void Slots_follow_open_hours_shift_and_existing_bookings_with_buffer()
    {
        var lan = new SlotCalculator.StaffAvailability(Lan, [(9 * 60, 12 * 60)], [new BusyInterval(10 * 60, 11 * 60)]); // ca 9–12, lịch 10:00–11:00
        var slots = SlotCalculator.Compute(Input([lan]));

        slots.Select(s => s.Start / 60.0).ShouldBe([9.0, 11.0]); // trước 9:00 ngoài ca; 9:30/10:xx chồng lịch; 11:30 vượt ca
    }

    [Fact]
    public void Buffer_is_counted_for_the_new_booking_too()
    {
        var lan = new SlotCalculator.StaffAvailability(Lan, [(8 * 60, 12 * 60)], [new BusyInterval(10 * 60, 11 * 60)]);
        var slots = SlotCalculator.Compute(Input([lan], buffer: 15));

        // 8:00, 8:30 (8:30+75 = 9:45 ok), 9:00 → 10:15 chồng 10:00 → loại; 11:00 → 12:15 vượt đóng? duration chỉ 60 → 12:00 ok, đệm sau giờ đóng vẫn được.
        slots.Select(s => s.Start).ShouldBe([8 * 60, 8 * 60 + 30, 11 * 60]);
    }

    [Fact]
    public void Step_and_earliest_start_are_respected()
    {
        var lan = new SlotCalculator.StaffAvailability(Lan, [(8 * 60, 12 * 60)], []);
        var slots = SlotCalculator.Compute(Input([lan], step: 60, earliest: 8 * 60 + 10));

        slots.Select(s => s.Start).ShouldBe([9 * 60, 10 * 60, 11 * 60]);
    }

    [Fact]
    public void Capacity_limits_concurrent_bookings_across_staff()
    {
        var lan = new SlotCalculator.StaffAvailability(Lan, [(8 * 60, 12 * 60)], []);
        var mai = new SlotCalculator.StaffAvailability(Mai, [(8 * 60, 12 * 60)], []);
        var busy = new[] { new BusyInterval(9 * 60, 10 * 60) }; // một giường đang dùng 9–10

        var oneBed = SlotCalculator.Compute(Input([lan, mai], busy, capacity: 1));
        oneBed.Where(s => s.Start is >= 8 * 60 + 30 and < 10 * 60).ShouldBeEmpty(); // 8:30–9:30 và 9:00–10:00, 9:30–10:30 đều chồng
        oneBed.Count(s => s.Start == 8 * 60).ShouldBe(2);

        var twoBeds = SlotCalculator.Compute(Input([lan, mai], busy, capacity: 2));
        twoBeds.Count(s => s.Start == 9 * 60).ShouldBe(2);
    }

    [Fact]
    public void Without_staff_only_capacity_matters()
    {
        var slots = SlotCalculator.Compute(Input([], [new BusyInterval(8 * 60, 9 * 60)], capacity: 1));
        slots.ShouldAllBe(s => s.StaffMemberId == null);
        slots.Select(s => s.Start).ShouldBe([9 * 60, 9 * 60 + 30, 10 * 60, 10 * 60 + 30, 11 * 60]);
    }

    [Fact]
    public void Nearest_picks_closest_distinct_starts()
    {
        var lan = new SlotCalculator.StaffAvailability(Lan, [(8 * 60, 12 * 60)], []);
        var mai = new SlotCalculator.StaffAvailability(Mai, [(8 * 60, 12 * 60)], []);
        var slots = SlotCalculator.Compute(Input([lan, mai]));

        SlotCalculator.Nearest(slots, 10 * 60 + 10, 3).Select(s => s.Start).ShouldBe([9 * 60 + 30, 10 * 60, 10 * 60 + 30]);
    }

    [Fact]
    public void Booking_helpers_parse_local_time_and_confirm_replies()
    {
        var zone = CrmClock.Zone("Asia/Ho_Chi_Minh");
        BookingOrchestrator.TryParseLocal("2026-10-20", "15:00", zone, out var start).ShouldBeTrue();
        start.ShouldBe(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        BookingOrchestrator.TryParseLocal("2026-10-20", null, zone, out _).ShouldBeFalse();
        BookingOrchestrator.TryParseLocal("mai", "15:00", zone, out _).ShouldBeFalse();

        BookingOrchestrator.IsConfirmReply("Ok nha!").ShouldBeTrue();
        BookingOrchestrator.IsConfirmReply("xác nhận").ShouldBeTrue();
        BookingOrchestrator.IsConfirmReply("ok cho chị hỏi thêm").ShouldBeFalse();

        CrmClock.FormatForCustomer(start, zone).ShouldBe("Thứ 3 20/10 lúc 15:00");
    }
}
