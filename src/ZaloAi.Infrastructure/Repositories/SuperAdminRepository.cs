using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

public sealed record TenantMemberRow(Guid UserId, string Email, string Name, TenantRole Role);

/// <param name="Month">Ngày 1 của tháng (giờ Việt Nam).</param>
public sealed record MonthlyCost(DateOnly Month, decimal CostUsd, int Calls);

/// <summary>
/// Truy vấn XUYÊN TENANT cho trang super admin (CLAUDE.md M6 — chỉ chủ dự án). Dùng IgnoreQueryFilters() CÓ CHỦ ĐÍCH: màn hình này
/// tổng hợp mọi DN. Mọi hàm kiểm <see cref="ITenantContext.IsSuperAdmin"/> trước khi đọc; chỉ trả số liệu tổng hợp và thông tin tài
/// khoản, không trả nội dung tin nhắn hay dữ liệu khách cuối. Ghi lên một DN cụ thể thì KHÔNG dùng lớp này: dùng repository thường
/// sau khi đặt tenant context sang DN đó (xem TenantAdminService).
/// </summary>
public sealed class SuperAdminRepository(AppDbContext db, ITenantContext tenantContext)
{
    public async Task<IReadOnlyList<Tenant>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        EnsureSuperAdmin();
        return await db.Tenants.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<Tenant?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureSuperAdmin();
        return await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<(Guid TenantId, TenantMemberRow Member)>> ListMembersAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        EnsureSuperAdmin();
        var rows = await (
            from m in db.Memberships.IgnoreQueryFilters()
            join u in db.Users on m.UserId equals u.Id
            where tenantId == null || m.TenantId == tenantId
            orderby m.Role descending, m.CreatedAt
            select new { m.TenantId, u.Id, u.Email, u.Name, m.Role }).ToListAsync(cancellationToken);
        return rows.Select(r => (r.TenantId, new TenantMemberRow(r.Id, r.Email, r.Name, r.Role))).ToList();
    }

    /// <summary>Số hội thoại thật (khách khác nhau có nhắn, không tính Chat thử) từ <paramref name="since"/>, theo DN.</summary>
    public async Task<Dictionary<Guid, int>> CountConversationsAsync(DateTimeOffset since, Guid? tenantId, CancellationToken cancellationToken)
    {
        EnsureSuperAdmin();
        var rows = await (
            from m in db.Messages.IgnoreQueryFilters()
            join c in db.Conversations.IgnoreQueryFilters() on m.ConversationId equals c.Id
            where m.Sender == MessageSender.Customer && m.CreatedAt >= since && !c.IsTest && (tenantId == null || m.TenantId == tenantId)
            group m.ConversationId by m.TenantId into g
            select new { TenantId = g.Key, Count = g.Distinct().Count() }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.TenantId, r => r.Count);
    }

    /// <summary>Chi phí AI theo DN và tháng (giờ Việt Nam) từ <paramref name="since"/>.</summary>
    public async Task<IReadOnlyList<(Guid TenantId, MonthlyCost Cost)>> MonthlyCostsAsync(DateTimeOffset since, Guid? tenantId, CancellationToken cancellationToken)
    {
        EnsureSuperAdmin();
        var rows = await db.UsageRecords.IgnoreQueryFilters()
            .Where(u => u.CreatedAt >= since && (tenantId == null || u.TenantId == tenantId))
            .Select(u => new { u.TenantId, u.CreatedAt, u.CostUsd })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => (r.TenantId, Month: MonthOf(r.CreatedAt)))
            .Select(g => (g.Key.TenantId, new MonthlyCost(g.Key.Month, g.Sum(r => r.CostUsd), g.Count())))
            .OrderBy(r => r.Item2.Month)
            .ToList();
    }

    /// <summary>DN đang hoạt động đã quá hạn thuê trước <paramref name="cutoff"/> (job tự khóa).</summary>
    public async Task<IReadOnlyList<Guid>> ListExpiredActiveAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        EnsureSuperAdmin();
        return await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Status == TenantStatus.Active && t.ExpiresAt != null && t.ExpiresAt < cutoff)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public static DateOnly MonthOf(DateTimeOffset at)
    {
        var local = at.ToOffset(TimeSpan.FromHours(7));
        return new DateOnly(local.Year, local.Month, 1);
    }

    private void EnsureSuperAdmin()
    {
        if (!tenantContext.IsSuperAdmin)
        {
            throw new ForbiddenException();
        }
    }
}
