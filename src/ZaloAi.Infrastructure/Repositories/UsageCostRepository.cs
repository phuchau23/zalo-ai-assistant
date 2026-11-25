using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Một lần gọi AI đã quy về kênh/nick: bản ghi mới có connection_id; bản ghi cũ lấy theo hội thoại.</summary>
public sealed record UsageCostRow(Guid? ConnectionId, DateTimeOffset CreatedAt, UsageKind Kind, decimal CostUsd, int InputTokens, int OutputTokens);

/// <summary>Kết quả công việc của một kênh/nick trong kỳ (để tính đ/người, đ/khách trả lời, đ/lịch hẹn).</summary>
public sealed record ConnectionActivity(Guid ConnectionId, int ContactsReached, int ContactsReplied, int Appointments);

/// <summary>Chi phí AI theo kênh/nick của MỘT doanh nghiệp (2026-11-15). Mọi truy vấn lọc tenant_id.</summary>
public sealed class UsageCostRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public async Task<IReadOnlyList<UsageCostRow>> RowsAsync(Guid tenantId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await (from u in Db.UsageRecords.AsNoTracking()
                      where u.TenantId == tenantId && u.CreatedAt >= since && u.CreatedAt < until
                      join c in Db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId) on u.ConversationId equals (Guid?)c.Id into cs
                      from c in cs.DefaultIfEmpty()
                      select new UsageCostRow(u.ConnectionId ?? (c == null ? null : c.ConnectionId), u.CreatedAt, u.Kind, u.CostUsd, u.InputTokens, u.OutputTokens))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectionActivity>> ActivityAsync(Guid tenantId, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var contacts = await (from m in Db.Messages.AsNoTracking()
                              join c in Db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
                              where m.TenantId == tenantId && c.TenantId == tenantId && c.ConnectionId != null && !c.IsTest
                                    && m.CreatedAt >= since && m.CreatedAt < until
                              group m by new { ConnectionId = c.ConnectionId!.Value, c.ContactId } into g
                              select new { g.Key.ConnectionId, Replied = g.Any(m => m.Sender == MessageSender.Customer) })
            .ToListAsync(cancellationToken);
        var appointments = await (from a in Db.Appointments.AsNoTracking()
                                  join c in Db.Conversations.AsNoTracking() on a.ConversationId equals (Guid?)c.Id
                                  where a.TenantId == tenantId && c.TenantId == tenantId && c.ConnectionId != null
                                        && a.CreatedAt >= since && a.CreatedAt < until && a.Status != AppointmentStatus.Cancelled
                                  group a by c.ConnectionId!.Value into g
                                  select new { ConnectionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(r => r.ConnectionId, r => r.Count, cancellationToken);

        var ids = contacts.Select(r => r.ConnectionId).Concat(appointments.Keys).Distinct();
        return ids.Select(id => new ConnectionActivity(
                id,
                contacts.Count(r => r.ConnectionId == id),
                contacts.Count(r => r.ConnectionId == id && r.Replied),
                appointments.GetValueOrDefault(id)))
            .ToList();
    }
}
