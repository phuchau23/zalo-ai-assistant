using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

public sealed record VoucherInput(string Code, VoucherKind Kind, long Value, long? MaxDiscount, long MinTotal, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, int? MaxUses, bool IsActive, string? Note);

/// <summary>Mã giảm giá (CRM C3): quản lý bởi chủ / quyền manage_catalog; áp vào hóa đơn ở <see cref="InvoiceService"/>.</summary>
public sealed class VoucherService(AppDbContext db, ITenantContext tenantContext, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<Voucher>> ListAsync(Guid tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Vouchers.AsNoTracking().Where(v => v.TenantId == tenantId);
        if (!includeInactive)
        {
            query = query.Where(v => v.IsActive);
        }

        return await query.OrderByDescending(v => v.CreatedAt).ToListAsync(cancellationToken);
    }

    public async Task<Voucher> CreateAsync(Guid tenantId, VoucherInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        var code = Normalize(input.Code);
        if (await Db.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code, cancellationToken))
        {
            throw new ConflictException($"Mã {code} đã có.", "duplicate_code");
        }

        var voucher = new Voucher { TenantId = tenantId, Code = code };
        Apply(voucher, input);
        Db.Vouchers.Add(voucher);
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "voucher.created", Target = code });
        await Db.SaveChangesAsync(cancellationToken);
        return voucher;
    }

    public async Task<Voucher> UpdateAsync(Guid tenantId, Guid voucherId, VoucherInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        var voucher = await Db.Vouchers.FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Id == voucherId, cancellationToken) ?? throw new NotFoundException();
        var code = Normalize(input.Code);
        if (code != voucher.Code && await Db.Vouchers.AnyAsync(v => v.TenantId == tenantId && v.Code == code, cancellationToken))
        {
            throw new ConflictException($"Mã {code} đã có.", "duplicate_code");
        }

        voucher.Code = code;
        Apply(voucher, input);
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "voucher.updated", Target = code });
        await Db.SaveChangesAsync(cancellationToken);
        return voucher;
    }

    /// <summary>Kiểm tra mã và tính số tiền giảm cho tổng <paramref name="basis"/>; không hợp lệ → ConflictException (voucher_invalid).</summary>
    public static async Task<(Voucher Voucher, long Amount)> EvaluateAsync(AppDbContext db, Guid tenantId, string code, long basis, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var normalized = Normalize(code);
        var voucher = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.Code == normalized, cancellationToken)
            ?? throw new ConflictException("Mã giảm giá không tồn tại.", "voucher_invalid");
        if (!voucher.IsActive || (voucher.ValidFrom is { } from && now < from) || (voucher.ValidTo is { } to && now > to)
            || (voucher.MaxUses is { } max && voucher.UsedCount >= max))
        {
            throw new ConflictException("Mã giảm giá hết hạn hoặc hết lượt.", "voucher_invalid");
        }

        if (basis < voucher.MinTotal)
        {
            throw new ConflictException($"Mã này áp dụng cho hóa đơn từ {voucher.MinTotal:N0} đ.", "voucher_min_total");
        }

        var amount = voucher.Kind == VoucherKind.Percent ? basis * voucher.Value / 100 : voucher.Value;
        if (voucher.MaxDiscount is { } cap)
        {
            amount = Math.Min(amount, cap);
        }

        return (voucher, Math.Clamp(amount, 0, basis));
    }

    public static string Normalize(string code) =>
        string.IsNullOrWhiteSpace(code) || code.Trim().Length > 50 ? throw new InvalidInputException("Mã giảm giá 1–50 ký tự.") : code.Trim().ToUpperInvariant();

    private void Apply(Voucher voucher, VoucherInput input)
    {
        if (input.Value <= 0 || (input.Kind == VoucherKind.Percent && input.Value > 100) || input.MinTotal < 0 || input.MaxDiscount is < 0 || input.MaxUses is < 1)
        {
            throw new InvalidInputException("Giá trị voucher không hợp lệ.");
        }

        voucher.Kind = input.Kind;
        voucher.Value = input.Value;
        voucher.MaxDiscount = input.Kind == VoucherKind.Percent ? input.MaxDiscount : null;
        voucher.MinTotal = input.MinTotal;
        voucher.ValidFrom = input.ValidFrom?.ToUniversalTime();
        voucher.ValidTo = input.ValidTo?.ToUniversalTime();
        voucher.MaxUses = input.MaxUses;
        voucher.IsActive = input.IsActive;
        voucher.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        _ = time;
    }
}
