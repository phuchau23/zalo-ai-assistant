using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>Thẻ trả trước (ví) của khách (CRM C3). Mọi thay đổi số dư là một giao dịch có số dư sau; không bao giờ âm.</summary>
public sealed class WalletService(AppDbContext db, ITenantContext tenantContext, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<(long Balance, IReadOnlyList<WalletTransaction> Transactions)> GetAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var wallet = await Db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == tenantId && w.ContactId == contactId, cancellationToken);
        var transactions = await Db.WalletTransactions.AsNoTracking().Where(t => t.TenantId == tenantId && t.ContactId == contactId)
            .OrderByDescending(t => t.At).Take(100).ToListAsync(cancellationToken);
        return (wallet?.Balance ?? 0, transactions);
    }

    /// <summary>Nạp tiền (đã thu ngoài hóa đơn hoặc qua hóa đơn tự nhập) + thưởng; ghi 2 giao dịch.</summary>
    public async Task<long> TopupAsync(Guid tenantId, Guid contactId, long amount, long bonus, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (amount <= 0 || bonus < 0)
        {
            throw new InvalidInputException("Số tiền nạp phải > 0, thưởng không âm.");
        }

        if (!await Db.Contacts.AnyAsync(c => c.TenantId == tenantId && c.Id == contactId, cancellationToken))
        {
            throw new NotFoundException();
        }

        var wallet = await GetOrCreateAsync(tenantId, contactId, cancellationToken);
        Add(tenantId, wallet, WalletTransactionKind.Topup, amount, null, note);
        if (bonus > 0)
        {
            Add(tenantId, wallet, WalletTransactionKind.Bonus, bonus, null, "Thưởng nạp");
        }

        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "wallet.topup", Target = $"{contactId}:{amount}+{bonus}" });
        await Db.SaveChangesAsync(cancellationToken);
        return wallet.Balance;
    }

    public async Task<long> AdjustAsync(Guid tenantId, Guid contactId, long delta, string reason, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (delta == 0 || string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidInputException("Cần số tiền khác 0 và lý do.");
        }

        var wallet = await GetOrCreateAsync(tenantId, contactId, cancellationToken);
        if (wallet.Balance + delta < 0)
        {
            throw new ConflictException("Số dư không đủ.", "wallet_insufficient");
        }

        Add(tenantId, wallet, WalletTransactionKind.Adjust, delta, null, reason.Trim());
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = "wallet.adjusted", Target = $"{contactId}:{delta}" });
        await Db.SaveChangesAsync(cancellationToken);
        return wallet.Balance;
    }

    internal async Task SpendAsync(Guid tenantId, Guid contactId, long amount, Guid invoiceId, Guid? byUserId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var wallet = await Db.Wallets.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.ContactId == contactId, cancellationToken);
        if (wallet is null || wallet.Balance < amount)
        {
            throw new ConflictException("Số dư ví không đủ.", "wallet_insufficient");
        }

        Add(tenantId, wallet, WalletTransactionKind.Spend, -amount, invoiceId, null, byUserId);
    }

    internal async Task RefundAsync(Guid tenantId, Guid contactId, long amount, Guid invoiceId, Guid? byUserId, CancellationToken cancellationToken)
    {
        var wallet = await GetOrCreateAsync(tenantId, contactId, cancellationToken);
        Add(tenantId, wallet, WalletTransactionKind.Refund, amount, invoiceId, null, byUserId);
    }

    private async Task<Wallet> GetOrCreateAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        var wallet = await Db.Wallets.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.ContactId == contactId, cancellationToken);
        if (wallet is null)
        {
            wallet = new Wallet { TenantId = tenantId, ContactId = contactId };
            Db.Wallets.Add(wallet);
        }

        return wallet;
    }

    private void Add(Guid tenantId, Wallet wallet, WalletTransactionKind kind, long amount, Guid? invoiceId, string? note, Guid? byUserId = null)
    {
        wallet.Balance += amount;
        Db.WalletTransactions.Add(new WalletTransaction
        {
            TenantId = tenantId,
            ContactId = wallet.ContactId,
            Kind = kind,
            Amount = amount,
            BalanceAfter = wallet.Balance,
            InvoiceId = invoiceId,
            Note = note,
            ByUserId = byUserId ?? _caller.UserId,
            At = time.GetUtcNow(),
        });
    }
}
