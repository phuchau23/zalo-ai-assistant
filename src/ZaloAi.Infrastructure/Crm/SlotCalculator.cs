namespace ZaloAi.Infrastructure.Crm;

/// <summary>Khoảng bận theo phút trong ngày [Start, End) (lịch đã có + phút dọn dẹp, nghỉ phép một phần ngày).</summary>
public readonly record struct BusyInterval(int Start, int End)
{
    public bool Overlaps(int start, int end) => start < End && end > Start;
}

/// <param name="StaffMemberId">Nhân viên có thể làm; null = không cần nhân viên (chỉ tài nguyên).</param>
/// <param name="Start">Phút trong ngày.</param>
public readonly record struct Slot(Guid? StaffMemberId, int Start);

/// <summary>
/// Tính khung giờ trống trong MỘT ngày, thuần túy (test được, không DB). Mọi thời gian là phút từ 00:00 giờ địa phương.
/// Khung trống = nằm trọn trong giờ mở cửa ∩ ca nhân viên, không chồng lịch của nhân viên đó, và tại mọi phút số lịch
/// cùng lúc của chi nhánh &lt; sức chứa (số giường/phòng; null = không giới hạn).
/// </summary>
public static class SlotCalculator
{
    public sealed record StaffAvailability(Guid StaffMemberId, IReadOnlyList<(int Start, int End)> WorkWindows, IReadOnlyList<BusyInterval> Busy);

    public sealed record Input(
        IReadOnlyList<(int Start, int End)> OpenWindows,
        IReadOnlyList<StaffAvailability> Staff,
        IReadOnlyList<BusyInterval> BranchBusy,
        int? Capacity,
        int DurationMinutes,
        int BufferMinutes,
        int StepMinutes,
        int EarliestStart);

    public static IReadOnlyList<Slot> Compute(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DurationMinutes <= 0 || input.StepMinutes <= 0)
        {
            return [];
        }

        var total = input.DurationMinutes + Math.Max(0, input.BufferMinutes);
        var slots = new List<Slot>();
        foreach (var (open, close) in input.OpenWindows)
        {
            var first = Math.Max(open, input.EarliestStart);
            first = (int)Math.Ceiling(first / (double)input.StepMinutes) * input.StepMinutes;
            for (var start = first; start + input.DurationMinutes <= close; start += input.StepMinutes)
            {
                var end = start + total;
                if (!HasCapacity(input, start, end))
                {
                    continue;
                }

                if (input.Staff.Count == 0)
                {
                    slots.Add(new Slot(null, start));
                    continue;
                }

                foreach (var staff in input.Staff)
                {
                    var inShift = staff.WorkWindows.Any(w => start >= w.Start && start + input.DurationMinutes <= w.End);
                    if (inShift && !staff.Busy.Any(b => b.Overlaps(start, end)))
                    {
                        slots.Add(new Slot(staff.StaffMemberId, start));
                    }
                }
            }
        }

        return slots;
    }

    /// <summary>Mỗi giờ bắt đầu chỉ giữ một dòng (nhân viên ít lịch nhất được ưu tiên) — cho bot gợi ý.</summary>
    public static IReadOnlyList<Slot> Distinct(IReadOnlyList<Slot> slots, IReadOnlyDictionary<Guid, int>? loadByStaff = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots
            .GroupBy(s => s.Start)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(s => s.StaffMemberId is { } id && loadByStaff is not null ? loadByStaff.GetValueOrDefault(id) : 0).First())
            .ToList();
    }

    /// <summary>Các giờ gần <paramref name="wanted"/> nhất (trước/sau), tối đa <paramref name="count"/>.</summary>
    public static IReadOnlyList<Slot> Nearest(IReadOnlyList<Slot> slots, int wanted, int count)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return Distinct(slots).OrderBy(s => Math.Abs(s.Start - wanted)).ThenBy(s => s.Start).Take(count).OrderBy(s => s.Start).ToList();
    }

    private static bool HasCapacity(Input input, int start, int end)
    {
        if (input.Capacity is not { } capacity)
        {
            return true;
        }

        // Số lịch chồng lên khung này tại điểm đông nhất.
        var overlapping = input.BranchBusy.Where(b => b.Overlaps(start, end)).ToList();
        if (overlapping.Count < capacity)
        {
            return true;
        }

        var points = overlapping.Select(b => Math.Max(b.Start, start)).Append(start).Distinct();
        return points.All(p => overlapping.Count(b => b.Start <= p && p < b.End) < capacity);
    }

    public static int Minutes(TimeOnly time) => time.Hour * 60 + time.Minute;

    public static TimeOnly ToTime(int minutes) => new(Math.Clamp(minutes, 0, 1439) / 60, Math.Clamp(minutes, 0, 1439) % 60);
}
