using Hangfire;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>
/// Báo Telegram khi danh sách SĐT của một nick sắp hết / đã hết (chủ dự án chốt 2026-11-17), để DN kịp dán thêm số cho bot chăm sóc
/// tiếp. Mỗi lần chỉ báo MỘT lần (cờ trong Redis):
/// - "Sắp hết": số người còn chờ ≤ số người mới nick nhận được trong 1 ngày — chỉ khi trước đó hàng chờ từng dài hơn mức này
///   (vừa dán 5 số thì không báo "sắp hết" ngay).
/// - "Hết": hàng chờ về 0 ở nick đã từng được giao số. Dán thêm số → hàng chờ dài lại → lần sau báo tiếp.
/// Nick đang mất kết nối thì bỏ qua (đã có tin báo riêng). Chạy cuối job hàng đợi (mỗi phút khi có việc) và job dọn dẹp (mỗi giờ).
/// </summary>
public sealed class OutreachQueueWatcher(
    OutreachRepository outreach,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    IDistributedStore store,
    IBackgroundJobClient jobs,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time)
{
    public const string QueueLow = "zp:queue_low";

    public const string QueueEmpty = "zp:queue_empty";

    private static readonly TimeSpan Remember = TimeSpan.FromDays(180);

    /// <returns>Các tin báo vừa xếp hàng gửi (nick, loại) — để test.</returns>
    public async Task<IReadOnlyList<(Guid ConnectionId, string Reason)>> CheckAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var assigned = (await outreach.StateCountsAsync(tenantId, cancellationToken))
            .GroupBy(r => r.ConnectionId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        var running = (await connections.ListAsync(tenantId, cancellationToken))
            .Where(c => c.Channel == ChannelKind.ZaloPersonal && c.Status == ConnectionStatus.Active)
            .Select(c => c.Id)
            .ToHashSet();
        var sent = new List<(Guid, string)>();
        foreach (var account in await personal.ListAccountsAsync(tenantId, cancellationToken))
        {
            var id = account.ConnectionId;
            if (!running.Contains(id) || assigned.GetValueOrDefault(id) == 0)
            {
                continue; // mất kết nối (đã báo riêng) / chưa từng được giao số
            }

            var queued = await outreach.CountQueuedAsync(tenantId, id, cancellationToken);
            var perDay = Math.Max(1, PersonalPacing.Effective(account, options.Value, now).NewContactsPerDay);
            var armed = $"zp:alert:queue-armed:{id:N}";
            var low = $"zp:alert:queue-low:{id:N}";
            var empty = $"zp:alert:queue-empty:{id:N}";
            if (queued > perDay)
            {
                // Còn nhiều người chờ: sẵn sàng báo "sắp hết" / "hết" cho lượt sau.
                await store.SetAsync(armed, "1", Remember, cancellationToken);
                await store.TakeAsync(low, cancellationToken);
                await store.TakeAsync(empty, cancellationToken);
            }
            else if (queued > 0)
            {
                await store.TakeAsync(empty, cancellationToken);
                if (await store.GetAsync(armed, cancellationToken) is not null && await store.SetIfNotExistsAsync(low, "1", Remember, cancellationToken))
                {
                    sent.Add(Enqueue(tenantId, id, QueueLow));
                }
            }
            else if (await store.SetIfNotExistsAsync(empty, "1", Remember, cancellationToken))
            {
                await store.TakeAsync(armed, cancellationToken);
                sent.Add(Enqueue(tenantId, id, QueueEmpty));
            }
        }

        return sent;
    }

    private (Guid, string) Enqueue(Guid tenantId, Guid connectionId, string reason)
    {
        jobs.Enqueue<ZaloPersonalAlertJob>(job => job.RunAsync(tenantId, connectionId, reason, CancellationToken.None));
        return (connectionId, reason);
    }
}
