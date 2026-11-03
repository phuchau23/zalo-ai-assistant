using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <param name="CustomerTextEnc">Tin khách mà bot không trả lời được (mã hóa); null nếu không tìm thấy.</param>
/// <param name="Reason">no_knowledge | low_confidence</param>
public sealed record UnansweredRow(Guid ConversationId, string? CustomerTextEnc, string Reason, DateTimeOffset At);

/// <summary>
/// Số liệu dashboard của chủ DN (CLAUDE.md M6). Chỉ đọc, luôn lọc tenant_id; bỏ hội thoại Chat thử (is_test).
/// Gộp theo ngày giờ Việt Nam làm ở <see cref="Dashboard.DashboardStats"/>.
/// </summary>
public sealed class DashboardRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Trần số tin đọc cho biểu đồ (đủ cho 30 ngày của một DN nhỏ/vừa).</summary>
    public const int MaxActivityRows = 100_000;

    private static readonly string[] UnansweredReasons = ["no_knowledge", "low_confidence"];

    /// <summary>Tin của khách (thật, không phải chat thử) trong khoảng [from, to): hội thoại + thời điểm.</summary>
    public async Task<IReadOnlyList<(Guid ConversationId, DateTimeOffset At)>> ListCustomerActivityAsync(
        Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await RealMessages(tenantId)
            .Where(m => m.Sender == MessageSender.Customer && m.CreatedAt >= from && m.CreatedAt < to)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.ConversationId, m.CreatedAt })
            .Take(MaxActivityRows)
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.ConversationId, r.CreatedAt)).ToList();
    }

    /// <summary>Hội thoại cần người trong khoảng: bot chuyển nhân viên (trace needsHuman) hoặc nhân viên có nhắn.</summary>
    public async Task<IReadOnlyList<Guid>> ListNeededHumanAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await RealMessages(tenantId)
            .Where(m => m.CreatedAt >= from && m.CreatedAt < to)
            .Where(m => m.Sender == MessageSender.Staff
                || (m.Sender == MessageSender.Bot && m.AiTraceJson != null && EF.Functions.JsonContains(m.AiTraceJson, """{"needsHuman":true}""")))
            .Select(m => m.ConversationId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>Khách mới (không tính chat thử) từ <paramref name="from"/>: tổng và số đã thành khách tiềm năng (Quan tâm trở lên).</summary>
    public async Task<(int Total, int Leads)> CountNewContactsAsync(Guid tenantId, DateTimeOffset from, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await Db.Contacts
            .Where(c => c.TenantId == tenantId && c.Channel != ChannelKind.Webchat && c.CreatedAt >= from)
            .GroupBy(c => c.LeadStatus == LeadStatus.Interested || c.LeadStatus == LeadStatus.Hot || c.LeadStatus == LeadStatus.Won)
            .Select(g => new { IsLead = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return (rows.Sum(r => r.Count), rows.Where(r => r.IsLead).Sum(r => r.Count));
    }

    /// <summary>Chi phí AI (USD) từ <paramref name="from"/>, gồm cả chat thử (vẫn tốn tiền thật).</summary>
    public async Task<decimal> SumAiCostAsync(Guid tenantId, DateTimeOffset from, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.UsageRecords
            .Where(u => u.TenantId == tenantId && u.CreatedAt >= from)
            .SumAsync(u => u.CostUsd, cancellationToken);
    }

    /// <summary>
    /// Câu bot không trả lời được (chuyển nhân viên vì thiếu dữ liệu / không chắc), mới nhất trước. Trace nằm ở tin bot cuối của lượt,
    /// câu hỏi = tin khách gần nhất trước tin đó trong cùng hội thoại.
    /// </summary>
    public async Task<IReadOnlyList<UnansweredRow>> ListUnansweredAsync(Guid tenantId, DateTimeOffset from, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var traces = await RealMessages(tenantId)
            .Where(m => m.Sender == MessageSender.Bot && m.CreatedAt >= from && m.AiTraceJson != null
                && (EF.Functions.JsonContains(m.AiTraceJson, """{"handoffReason":"no_knowledge"}""")
                    || EF.Functions.JsonContains(m.AiTraceJson, """{"handoffReason":"low_confidence"}""")))
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new
            {
                m.ConversationId,
                m.CreatedAt,
                LowConfidence = EF.Functions.JsonContains(m.AiTraceJson!, """{"handoffReason":"low_confidence"}"""),
                Question = Db.Messages
                    .Where(c => c.TenantId == tenantId && c.ConversationId == m.ConversationId && c.Sender == MessageSender.Customer && c.CreatedAt <= m.CreatedAt)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => c.ContentEnc)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return traces
            .Select(t => new UnansweredRow(t.ConversationId, t.Question, t.LowConfidence ? UnansweredReasons[1] : UnansweredReasons[0], t.CreatedAt))
            .ToList();
    }

    private IQueryable<Message> RealMessages(Guid tenantId) =>
        from m in Db.Messages
        join c in Db.Conversations on m.ConversationId equals c.Id
        where m.TenantId == tenantId && c.TenantId == tenantId && !c.IsTest
        select m;
}
