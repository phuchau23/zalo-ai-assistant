using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

public sealed record BankAccountInput(Guid? BranchId, string BankBin, string BankName, string AccountNo, string AccountName, bool IsDefault);

public sealed record BankAccountView(TenantBankAccount Account, string AccountNoMasked);

/// <summary>Tài khoản nhận chuyển khoản cho VietQR (CRM C3). Số tài khoản mã hóa; chỉ chủ DN quản lý.</summary>
public sealed class BankAccountService(AppDbContext db, ITenantContext tenantContext, IFieldEncryptor encryptor) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<BankAccountView>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var accounts = await Db.TenantBankAccounts.AsNoTracking().Where(a => a.TenantId == tenantId).OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        return accounts.Select(a => new BankAccountView(a, Mask(encryptor.Decrypt(a.AccountNoEnc)))).ToList();
    }

    public async Task<BankAccountView> CreateAsync(Guid tenantId, BankAccountInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        if (input.BranchId is { } b && !await Db.Branches.AnyAsync(x => x.TenantId == tenantId && x.Id == b, cancellationToken))
        {
            throw new NotFoundException();
        }

        var account = new TenantBankAccount
        {
            TenantId = tenantId,
            BranchId = input.BranchId,
            BankBin = input.BankBin.Trim(),
            BankName = input.BankName.Trim(),
            AccountNoEnc = encryptor.Encrypt(Digits(input.AccountNo)),
            AccountName = input.AccountName.Trim().ToUpperInvariant(),
            IsDefault = input.IsDefault || !await Db.TenantBankAccounts.AnyAsync(a => a.TenantId == tenantId, cancellationToken),
        };
        if (account.IsDefault)
        {
            await Db.TenantBankAccounts.Where(a => a.TenantId == tenantId && a.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false), cancellationToken);
        }

        Db.TenantBankAccounts.Add(account);
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "bank_account.created", Target = account.Id.ToString() });
        await Db.SaveChangesAsync(cancellationToken);
        return new BankAccountView(account, Mask(Digits(input.AccountNo)));
    }

    public async Task DeleteAsync(Guid tenantId, Guid accountId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var removed = await Db.TenantBankAccounts.Where(a => a.TenantId == tenantId && a.Id == accountId).ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
        {
            throw new NotFoundException();
        }

        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "bank_account.deleted", Target = accountId.ToString() });
        await Db.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(BankAccountInput input)
    {
        if (input.BankBin.Trim().Length is < 6 or > 10 || !input.BankBin.Trim().All(char.IsAsciiDigit)
            || Digits(input.AccountNo).Length is < 6 or > 30 || string.IsNullOrWhiteSpace(input.BankName) || string.IsNullOrWhiteSpace(input.AccountName))
        {
            throw new InvalidInputException("Cần BIN ngân hàng (6 số), tên ngân hàng, số tài khoản (6–30 số) và tên chủ tài khoản.");
        }
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    private static string Mask(string accountNo) => accountNo.Length <= 4 ? accountNo : new string('*', accountNo.Length - 4) + accountNo[^4..];
}
