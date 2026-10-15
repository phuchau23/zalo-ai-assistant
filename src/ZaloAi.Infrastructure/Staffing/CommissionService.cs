using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Staffing;

public sealed record CommissionRuleInput(Guid? StaffMemberId, CommissionScope Scope, Guid? CatalogItemId, CommissionKind Kind, long Value, bool IsActive, string? Note);

public sealed record CommissionStatement(Guid StaffMemberId, string StaffName, string Period, long Total, long Paid, IReadOnlyList<CommissionEntry> Entries);

/// <summary>
/// Hoa hồng (CRM C5): quy tắc theo nhân viên / loại / mục; khoản phát sinh khi hóa đơn thu đủ (dòng có người thực hiện) và khi dùng
/// buổi gói (người làm buổi đó). Hủy hóa đơn → thu hồi. Bảng tháng theo kỳ "yyyy-MM" giờ DN; đánh dấu đã trả theo kỳ.
/// </summary>
public sealed class CommissionService(AppDbContext db, ITenantContext tenantContext, CrmSettingsRepository settingsRepository, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<CommissionRule>> RulesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.CommissionRules.AsNoTracking().Where(r => r.TenantId == tenantId).OrderBy(r => r.StaffMemberId).ThenBy(r => r.Scope).ToListAsync(cancellationToken);
    }

    public async Task<CommissionRule> SaveRuleAsync(Guid tenantId, Guid? ruleId, CommissionRuleInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Value < 0 || (input.Kind == CommissionKind.Percent && input.Value > 100) || (input.Scope == CommissionScope.Item) != (input.CatalogItemId is not null))
        {
            throw new InvalidInputException("Giá trị hoa hồng không hợp lệ (mục cụ thể cần chọn mục; % ≤ 100).");
        }

        if (input.StaffMemberId is { } s && !await Db.StaffMembers.AnyAsync(m => m.TenantId == tenantId && m.Id == s, cancellationToken))
        {
            throw new NotFoundException();
        }

        if (input.CatalogItemId is { } c && !await Db.CatalogItems.AnyAsync(m => m.TenantId == tenantId && m.Id == c, cancellationToken))
        {
            throw new NotFoundException();
        }

        var rule = ruleId is { } id
            ? await Db.CommissionRules.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken) ?? throw new NotFoundException()
            : Db.CommissionRules.Add(new CommissionRule { TenantId = tenantId }).Entity;
        rule.StaffMemberId = input.StaffMemberId;
        rule.Scope = input.Scope;
        rule.CatalogItemId = input.Scope == CommissionScope.Item ? input.CatalogItemId : null;
        rule.Kind = input.Kind;
        rule.Value = input.Value;
        rule.IsActive = input.IsActive;
        rule.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "commission_rule.saved", Target = rule.Id.ToString() });
        await Db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task DeleteRuleAsync(Guid tenantId, Guid ruleId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (await Db.CommissionRules.Where(r => r.TenantId == tenantId && r.Id == ruleId).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            throw new NotFoundException();
        }
    }

    /// <summary>Khoản hoa hồng cho một dòng doanh thu (gọi trong transaction của hóa đơn / buổi gói; không SaveChanges).</summary>
    internal async Task AccrueAsync(Guid tenantId, Guid staffMemberId, CatalogKind? kind, Guid? catalogItemId, long basis, Guid? invoiceId, Guid? invoiceLineId, Guid? redemptionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rules = await Db.CommissionRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsActive && (r.StaffMemberId == null || r.StaffMemberId == staffMemberId))
            .ToListAsync(cancellationToken);
        var rule = Resolve(rules, staffMemberId, kind, catalogItemId);
        if (rule is null)
        {
            return;
        }

        var amount = rule.Kind == CommissionKind.Percent ? basis * rule.Value / 100 : rule.Value;
        if (amount <= 0)
        {
            return;
        }

        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        Db.CommissionEntries.Add(new CommissionEntry
        {
            TenantId = tenantId,
            StaffMemberId = staffMemberId,
            InvoiceId = invoiceId,
            InvoiceLineId = invoiceLineId,
            RedemptionId = redemptionId,
            RuleId = rule.Id,
            Basis = basis,
            Amount = amount,
            Period = CrmClock.ToLocal(now, CrmClock.Zone(settings.TimeZone)).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            At = now,
        });
    }

    internal async Task RevertForInvoiceAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var now = time.GetUtcNow();
        var lineIds = await Db.InvoiceLines.Where(l => l.TenantId == tenantId && l.InvoiceId == invoiceId).Select(l => l.Id).ToListAsync(cancellationToken);
        var redemptionIds = await Db.PackageRedemptions.Where(r => r.TenantId == tenantId && r.InvoiceLineId != null && lineIds.Contains(r.InvoiceLineId.Value)).Select(r => r.Id).ToListAsync(cancellationToken);
        await Db.CommissionEntries
            .Where(e => e.TenantId == tenantId && e.RevertedAt == null && ((e.InvoiceId == invoiceId) || (e.RedemptionId != null && redemptionIds.Contains(e.RedemptionId.Value))))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.RevertedAt, now), cancellationToken);
    }

    public async Task<IReadOnlyList<CommissionStatement>> StatementsAsync(Guid tenantId, string period, Guid? staffMemberId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ValidatePeriod(period);
        var query = Db.CommissionEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.Period == period && e.RevertedAt == null);
        if (staffMemberId is { } s)
        {
            query = query.Where(e => e.StaffMemberId == s);
        }

        var entries = await query.OrderBy(e => e.At).ToListAsync(cancellationToken);
        var staffIds = entries.Select(e => e.StaffMemberId).Distinct().ToList();
        var staff = await Db.StaffMembers.AsNoTracking().Where(m => m.TenantId == tenantId && staffIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.DisplayName, cancellationToken);
        return entries.GroupBy(e => e.StaffMemberId)
            .Select(g => new CommissionStatement(g.Key, staff.GetValueOrDefault(g.Key, "?"), period, g.Sum(e => e.Amount), g.Where(e => e.PaidAt != null).Sum(e => e.Amount), g.ToList()))
            .OrderByDescending(s => s.Total).ToList();
    }

    public async Task<int> MarkPaidAsync(Guid tenantId, Guid staffMemberId, string period, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ValidatePeriod(period);
        var now = time.GetUtcNow();
        var count = await Db.CommissionEntries
            .Where(e => e.TenantId == tenantId && e.StaffMemberId == staffMemberId && e.Period == period && e.RevertedAt == null && e.PaidAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PaidAt, now), cancellationToken);
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "commission.paid", Target = $"{staffMemberId}:{period}:{count}" });
        await Db.SaveChangesAsync(cancellationToken);
        return count;
    }

    /// <summary>Chọn quy tắc theo thứ tự ưu tiên (thuần, test được).</summary>
    public static CommissionRule? Resolve(IReadOnlyList<CommissionRule> rules, Guid staffMemberId, CatalogKind? kind, Guid? catalogItemId)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var kindScope = kind switch
        {
            CatalogKind.Service => CommissionScope.Service,
            CatalogKind.Package => CommissionScope.Package,
            CatalogKind.Product => CommissionScope.Product,
            _ => (CommissionScope?)null,
        };
        CommissionRule? Find(bool own, CommissionScope scope, bool item) => rules.FirstOrDefault(r =>
            r.IsActive && (own ? r.StaffMemberId == staffMemberId : r.StaffMemberId == null) && r.Scope == scope && (!item || r.CatalogItemId == catalogItemId));

        return (catalogItemId is null ? null : Find(true, CommissionScope.Item, true) ?? Find(false, CommissionScope.Item, true))
            ?? (kindScope is { } ks ? Find(true, ks, false) ?? Find(false, ks, false) : null)
            ?? Find(true, CommissionScope.All, false)
            ?? Find(false, CommissionScope.All, false);
    }

    private static void ValidatePeriod(string period)
    {
        if (!DateTime.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new InvalidInputException("Kỳ dạng yyyy-MM.");
        }
    }
}
