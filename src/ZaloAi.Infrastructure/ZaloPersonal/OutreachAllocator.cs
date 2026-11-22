namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Sức của một nick khi chia số: nhận được bao nhiêu người mới mỗi ngày, đang có bao nhiêu người chờ.</summary>
public sealed record NickCapacity(Guid ConnectionId, int NewContactsPerDay, int Queued);

/// <summary>Kết quả chia cho một nick: số người nhận thêm, tổng hàng chờ sau khi chia, dự kiến bao nhiêu ngày đi hết.</summary>
public sealed record NickAllocation(Guid ConnectionId, int Assigned, int QueuedAfter, int EtaDays);

/// <summary>
/// Chia danh sách SĐT cho nhiều nick (chủ dự án chốt 2026-11-15) — "Weighted Least-Loaded", như xếp khách vào quầy thu ngân sẽ xong
/// sớm nhất: mỗi người giao cho nick có (hàng chờ + 1) ÷ (người mới/ngày) nhỏ nhất → các nick xong hàng chờ gần cùng lúc, nick khỏe
/// nhận nhiều hơn. Người đã quen một nick (từng được nick đó nhắn/mời) giữ nguyên nick đó ("Sticky"), miễn nick đó còn trong danh sách.
/// Hàm thuần, không đọc DB: nick nào được chia (đang chạy, không bị phanh "tạm dừng") do nơi gọi lọc trước.
/// </summary>
public static class OutreachAllocator
{
    /// <param name="preferred">Mỗi phần tử ứng với một người theo thứ tự dán: nick người đó đã quen, hoặc null.</param>
    /// <param name="nicks">Các nick được chia (ít nhất 1).</param>
    /// <returns>Nick được giao cho từng người (cùng thứ tự với <paramref name="preferred"/>) và tổng kết theo nick.</returns>
    public static (IReadOnlyList<Guid> Assignments, IReadOnlyList<NickAllocation> Summary) Allocate(
        IReadOnlyList<Guid?> preferred, IReadOnlyList<NickCapacity> nicks)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(nicks);
        if (nicks.Count == 0)
        {
            throw new ArgumentException("Cần ít nhất một nick để chia.", nameof(nicks));
        }

        var load = nicks.ToDictionary(n => n.ConnectionId, n => n.Queued);
        var assigned = nicks.ToDictionary(n => n.ConnectionId, _ => 0);
        var result = new Guid[preferred.Count];

        // Lượt 1: người quen nick nào giữ nick đó (tính vào tải trước khi chia người mới).
        var pending = new List<int>();
        for (var i = 0; i < preferred.Count; i++)
        {
            if (preferred[i] is { } nick && load.TryGetValue(nick, out var current))
            {
                result[i] = nick;
                load[nick] = current + 1;
                assigned[nick]++;
            }
            else
            {
                pending.Add(i);
            }
        }

        // Lượt 2: người còn lại → nick sẽ đi hết hàng chờ sớm nhất nếu nhận thêm người này.
        foreach (var i in pending)
        {
            var best = nicks[0];
            var bestScore = double.MaxValue;
            foreach (var n in nicks)
            {
                var score = (load[n.ConnectionId] + 1) / (double)PerDay(n);
                if (score < bestScore - 1e-9 || (Math.Abs(score - bestScore) < 1e-9 && load[n.ConnectionId] < load[best.ConnectionId]))
                {
                    best = n;
                    bestScore = score;
                }
            }

            result[i] = best.ConnectionId;
            load[best.ConnectionId]++;
            assigned[best.ConnectionId]++;
        }

        var summary = nicks
            .Select(n => new NickAllocation(n.ConnectionId, assigned[n.ConnectionId], load[n.ConnectionId], Eta(load[n.ConnectionId], PerDay(n))))
            .ToList();
        return (result, summary);
    }

    /// <summary>Số ngày để đi hết hàng chờ khi mỗi ngày nhận được <paramref name="perDay"/> người mới.</summary>
    public static int Eta(int queued, int perDay) => queued <= 0 ? 0 : (int)Math.Ceiling(queued / (double)Math.Max(1, perDay));

    private static int PerDay(NickCapacity n) => Math.Max(1, n.NewContactsPerDay);
}
