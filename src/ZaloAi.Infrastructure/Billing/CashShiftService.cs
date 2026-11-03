using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

public sealed record CashShiftSummary(CashShift Shift, long CashIn, long CashOut, long Transfer, long Card, long Wallet, int InvoiceCount);

/// <summary>Ca thu ngân (CRM C3): mở ca với tiền đầu ca; chốt ca = đối soát tiền mặt (đầu ca + thu − hoàn) với tiền đếm.</summary>
public sealed class CashShiftService(AppDbContext db, ITenantContext tenantContext, BranchService branches, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<CashShiftSummary?> CurrentAsync(Guid tenantId, Guid branchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var shift = await Db.CashShifts.AsNoTracking().Where(s => s.TenantId == tenantId && s.BranchId == branchId && s.ClosedAt == null)
            .OrderByDescending(s => s.OpenedAt).FirstOrDefaultAsync(cancellationToken);
        return shift is null ? null : await SummarizeAsync(tenantId, shift, cancellationToken);
    }

    public async Task<IReadOnlyList<CashShiftSummary>> ListAsync(Guid tenantId, Guid? branchId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.CashShifts.AsNoTracking().Where(s => s.TenantId == tenantId);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(s => s.BranchId == b);
        }

        var shifts = await query.OrderByDescending(s => s.OpenedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        var result = new List<CashShiftSummary>();
        foreach (var shift in shifts)
        {
            result.Add(await SummarizeAsync(tenantId, shift, cancellationToken));
        }

        return result;
    }

    public async Task<CashShift> OpenAsync(Guid tenantId, Guid branchId, long openingCash, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        await branches.RequireAsync(tenantId, branchId, cancellationToken);
        if (openingCash < 0)
        {
            throw new InvalidInputException("Tiền đầu ca không âm.");
        }

        if (await Db.CashShifts.AnyAsync(s => s.TenantId == tenantId && s.BranchId == branchId && s.ClosedAt == null, cancellationToken))
        {
            throw new ConflictException("Chi nhánh đang có ca chưa chốt.", "shift_open");
        }

        var shift = new CashShift { TenantId = tenantId, BranchId = branchId, OpenedByUserId = _caller.UserId ?? Guid.Empty, OpenedAt = time.GetUtcNow(), OpeningCash = openingCash };
        Db.CashShifts.Add(shift);
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "cash_shift.opened", Target = shift.Id.ToString() });
        await Db.SaveChangesAsync(cancellationToken);
        return shift;
    }

    public async Task<CashShiftSummary> CloseAsync(Guid tenantId, Guid shiftId, long countedCash, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var shift = await Db.CashShifts.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == shiftId, cancellationToken) ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && shift.BranchId != only)
        {
            throw new NotFoundException();
        }

        if (shift.ClosedAt is not null)
        {
            throw new ConflictException("Ca đã chốt.", "shift_closed");
        }

        var summary = await SummarizeAsync(tenantId, shift, cancellationToken);
        shift.ExpectedCash = shift.OpeningCash + summary.CashIn - summary.CashOut;
        shift.CountedCash = countedCash;
        shift.ClosedAt = time.GetUtcNow();
        shift.ClosedByUserId = _caller.UserId;
        shift.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "cash_shift.closed", Target = $"{shift.Id}:{shift.ExpectedCash}:{countedCash}" });
        await Db.SaveChangesAsync(cancellationToken);
        return await SummarizeAsync(tenantId, shift, cancellationToken);
    }

    private async Task<CashShiftSummary> SummarizeAsync(Guid tenantId, CashShift shift, CancellationToken cancellationToken)
    {
        var payments = await Db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId && p.CashShiftId == shift.Id).ToListAsync(cancellationToken);
        var cashIn = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        var cashOut = -payments.Where(p => p.Method == PaymentMethod.Refund && p.Reference != null && p.Reference.StartsWith("cash", StringComparison.Ordinal)).Sum(p => p.Amount);
        var invoices = await Db.Invoices.CountAsync(i => i.TenantId == tenantId && i.CashShiftId == shift.Id && i.Status != InvoiceStatus.Void, cancellationToken);
        return new CashShiftSummary(
            shift,
            cashIn,
            cashOut,
            payments.Where(p => p.Method == PaymentMethod.Transfer).Sum(p => p.Amount),
            payments.Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount),
            payments.Where(p => p.Method == PaymentMethod.Wallet).Sum(p => p.Amount),
            invoices);
    }

    internal static string Describe(DateTimeOffset at, TimeZoneInfo zone) => CrmClock.FormatFull(at, zone);
}
