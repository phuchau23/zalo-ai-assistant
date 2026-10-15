using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

/// <param name="CatalogItemId">null với dòng tự nhập (Kind = Custom) hoặc dòng dùng buổi gói (CustomerPackageId).</param>
public sealed record InvoiceLineInput(Guid? CatalogItemId, Guid? CustomerPackageId, string? Description, int Quantity, long? UnitPrice, long Discount, Guid? PerformerStaffId);

public sealed record InvoiceInput(Guid? BranchId, Guid? ContactId, Guid? AppointmentId, IReadOnlyList<InvoiceLineInput> Lines, string? Note);

public sealed record DiscountInput(long Amount, string? Reason, string? VoucherCode);

public sealed record PaymentInput(PaymentMethod Method, long Amount, string? Reference);

public sealed record InvoiceView(Invoice Invoice, IReadOnlyList<InvoiceLine> Lines, IReadOnlyList<Payment> Payments, string? ContactName, string? ContactPhone, string BranchName, string? Note, string? VoucherCode);

public sealed record InvoiceSummary(Invoice Invoice, string? ContactName, string BranchName, int LineCount);

/// <summary>
/// Hóa đơn và thu tiền (CRM C3). Quy tắc tiền (CLAUDE.md: AI chỉ đề xuất, hệ thống mới chốt — ở đây là nhân viên thao tác, hệ thống
/// kiểm tra): giảm giá quá ngưỡng cần quyền apply_discount_over_limit; hủy/hoàn cần quyền riêng; mọi thao tác có nhật ký audit.
/// Thu đủ tiền → ghi doanh thu (dịch vụ/sản phẩm), tạo gói cho khách (dòng gói), trừ ví; dòng dùng buổi gói → trừ buổi + doanh thu gói.
/// Hủy hóa đơn → hoàn buổi, thu hồi gói chưa dùng, hoàn ví, doanh thu âm. Hoàn tiền một phần → ghi payment âm + doanh thu âm.
/// </summary>
public sealed class InvoiceService(
    AppDbContext db,
    ITenantContext tenantContext,
    BranchService branches,
    CrmSettingsRepository settingsRepository,
    PackageService packages,
    WalletService wallets,
    IFieldEncryptor encryptor,
    TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<InvoiceSummary>> ListAsync(Guid tenantId, Guid? branchId, DateTimeOffset from, DateTimeOffset to, InvoiceStatus? status, Guid? contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        from = from.ToUniversalTime();
        to = to.ToUniversalTime();
        var query = Db.Invoices.AsNoTracking().Where(i => i.TenantId == tenantId && i.IssuedAt >= from && i.IssuedAt < to);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(i => i.BranchId == b);
        }

        if (status is { } s)
        {
            query = query.Where(i => i.Status == s);
        }

        if (contactId is { } c)
        {
            query = query.Where(i => i.ContactId == c);
        }

        var list = await query.OrderByDescending(i => i.IssuedAt).Take(1000).ToListAsync(cancellationToken);
        var ids = list.Select(i => i.Id).ToList();
        var lineCounts = await Db.InvoiceLines.Where(l => l.TenantId == tenantId && ids.Contains(l.InvoiceId)).GroupBy(l => l.InvoiceId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var contactIds = list.Where(i => i.ContactId != null).Select(i => i.ContactId!.Value).Distinct().ToList();
        var contacts = await Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var branchNames = await Db.Branches.AsNoTracking().Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        return list.Select(i => new InvoiceSummary(
            i,
            i.ContactId is { } cid && contacts.TryGetValue(cid, out var contact) ? ContactView.Name(contact, ContactView.LeadFields(contact, encryptor)) : null,
            branchNames.GetValueOrDefault(i.BranchId, ""),
            lineCounts.GetValueOrDefault(i.Id))).ToList();
    }

    public async Task<InvoiceView> GetAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: false, cancellationToken);
        return await ViewAsync(tenantId, invoice, cancellationToken);
    }

    /// <summary>Hóa đơn nháp từ lịch hẹn: các dịch vụ của lịch; dịch vụ nào khách còn gói đang dùng → dòng dùng buổi gói (giá 0).</summary>
    public async Task<Invoice> CreateFromAppointmentAsync(Guid tenantId, Guid appointmentId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var appointment = await Db.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == appointmentId, cancellationToken) ?? throw new NotFoundException();
        if (await Db.Invoices.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.AppointmentId == appointmentId && i.Status != InvoiceStatus.Void, cancellationToken) is { } existing)
        {
            return existing;
        }

        var items = await Db.AppointmentItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppointmentId == appointmentId).OrderBy(i => i.Position).ToListAsync(cancellationToken);
        var active = await packages.ActiveForContactAsync(tenantId, appointment.ContactId, cancellationToken);
        var lines = new List<InvoiceLineInput>();
        foreach (var item in items)
        {
            var covering = active.FirstOrDefault(p => p.Package.Remaining > 0 && p.ServiceIds.Contains(item.CatalogItemId));
            if (covering is not null)
            {
                lines.Add(new InvoiceLineInput(item.CatalogItemId, covering.Package.Id, item.Name, 1, 0, 0, appointment.StaffMemberId));
                active = active.Select(p => p.Package.Id == covering.Package.Id ? p with { Package = Clone(p.Package, -1) } : p).ToList();
            }
            else
            {
                lines.Add(new InvoiceLineInput(item.CatalogItemId, null, null, 1, null, 0, appointment.StaffMemberId));
            }
        }

        return await CreateAsync(tenantId, new InvoiceInput(appointment.BranchId, appointment.ContactId, appointmentId, lines, null), cancellationToken);
    }

    public async Task<Invoice> CreateAsync(Guid tenantId, InvoiceInput input, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(input);
        var branch = input.BranchId is { } bid ? await branches.RequireAsync(tenantId, bid, cancellationToken) : await branches.EnsureDefaultAsync(tenantId, cancellationToken);
        if (input.ContactId is { } cid && !await Db.Contacts.AnyAsync(c => c.TenantId == tenantId && c.Id == cid, cancellationToken))
        {
            throw new NotFoundException();
        }

        var invoice = new Invoice
        {
            TenantId = tenantId,
            BranchId = branch.Id,
            ContactId = input.ContactId,
            AppointmentId = input.AppointmentId,
            CashierUserId = _caller.UserId,
            CashShiftId = await OpenShiftIdAsync(tenantId, branch.Id, cancellationToken),
            IssuedAt = time.GetUtcNow(),
            NoteEnc = string.IsNullOrWhiteSpace(input.Note) ? null : encryptor.Encrypt(input.Note.Trim()),
        };

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        invoice.Number = await NextNumberAsync(tenantId, cancellationToken);
        Db.Invoices.Add(invoice);
        await ReplaceLinesAsync(tenantId, invoice, input.Lines, cancellationToken);
        Audit(tenantId, "invoice.created", invoice.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoice;
    }

    public async Task<Invoice> UpdateLinesAsync(Guid tenantId, Guid invoiceId, IReadOnlyList<InvoiceLineInput> lines, string? note, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        EnsureOpen(invoice);
        await Db.InvoiceLines.Where(l => l.TenantId == tenantId && l.InvoiceId == invoiceId).ExecuteDeleteAsync(cancellationToken);
        await ReplaceLinesAsync(tenantId, invoice, lines, cancellationToken);
        invoice.NoteEnc = string.IsNullOrWhiteSpace(note) ? null : encryptor.Encrypt(note.Trim());
        Audit(tenantId, "invoice.lines_updated", invoice.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        return invoice;
    }

    /// <summary>Giảm giá cấp hóa đơn và/hoặc voucher. Giảm (gồm dòng) vượt ngưỡng DN → cần quyền apply_discount_over_limit.</summary>
    public async Task<Invoice> ApplyDiscountAsync(Guid tenantId, Guid invoiceId, DiscountInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        EnsureOpen(invoice);
        if (input.Amount < 0 || input.Amount > invoice.Subtotal)
        {
            throw new InvalidInputException("Giảm giá phải từ 0 đến tổng hóa đơn.");
        }

        Voucher? voucher = null;
        long voucherAmount = 0;
        if (!string.IsNullOrWhiteSpace(input.VoucherCode))
        {
            (voucher, voucherAmount) = await VoucherService.EvaluateAsync(Db, tenantId, input.VoucherCode, invoice.Subtotal - input.Amount, time.GetUtcNow(), cancellationToken);
        }

        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var lineDiscounts = await Db.InvoiceLines.Where(l => l.TenantId == tenantId && l.InvoiceId == invoiceId).SumAsync(l => l.Discount, cancellationToken);
        var gross = invoice.Subtotal + lineDiscounts;
        var totalDiscount = lineDiscounts + input.Amount; // voucher là chương trình của DN, không tính vào ngưỡng duyệt
        if (gross > 0 && totalDiscount * 100 > gross * settings.DiscountApprovalPercent && !_caller.Can(TenantPermissions.ApplyDiscountOverLimit))
        {
            throw new ForbiddenException();
        }

        invoice.DiscountAmount = input.Amount;
        invoice.DiscountReason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        invoice.DiscountApprovedBy = totalDiscount * 100 > gross * settings.DiscountApprovalPercent ? _caller.UserId : null;
        invoice.VoucherId = voucher?.Id;
        invoice.VoucherAmount = voucherAmount;
        Recalculate(invoice);
        Audit(tenantId, "invoice.discount_applied", $"{invoice.Id}:{input.Amount}:{voucher?.Code}");
        await Db.SaveChangesAsync(cancellationToken);
        return invoice;
    }

    /// <summary>Thu tiền (nhiều lần được). Ví: trừ số dư khách. Đủ tiền → hóa đơn Paid và ghi nhận doanh thu, gói, buổi.</summary>
    public async Task<Invoice> AddPaymentAsync(Guid tenantId, Guid invoiceId, PaymentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        if (invoice.Status is InvoiceStatus.Void or InvoiceStatus.Refunded)
        {
            throw new ConflictException("Hóa đơn đã hủy / đã hoàn.", "invoice_state");
        }

        if (input.Method == PaymentMethod.Refund || input.Amount <= 0)
        {
            throw new InvalidInputException("Số tiền thu phải lớn hơn 0.");
        }

        if (input.Amount > invoice.Due)
        {
            throw new ConflictException("Số tiền vượt phần còn phải thu.", "overpayment");
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (input.Method == PaymentMethod.Wallet)
        {
            if (invoice.ContactId is not { } contactId)
            {
                throw new InvalidInputException("Hóa đơn chưa gắn khách, không trừ ví được.");
            }

            await wallets.SpendAsync(tenantId, contactId, input.Amount, invoice.Id, _caller.UserId, cancellationToken);
        }

        Db.Payments.Add(new Payment
        {
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = input.Method,
            Amount = input.Amount,
            Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim(),
            ReceivedByUserId = _caller.UserId,
            CashShiftId = invoice.CashShiftId ?? await OpenShiftIdAsync(tenantId, invoice.BranchId, cancellationToken),
            ReceivedAt = now,
        });
        invoice.PaidAmount += input.Amount;
        var wasPaid = invoice.Status == InvoiceStatus.Paid;
        invoice.Status = invoice.Due == 0 ? InvoiceStatus.Paid : InvoiceStatus.Partial;
        if (invoice.Status == InvoiceStatus.Paid && !wasPaid)
        {
            invoice.PaidAt = now;
            await SettleAsync(tenantId, invoice, cancellationToken);
        }

        Audit(tenantId, "invoice.payment_added", $"{invoice.Id}:{input.Method.ToString().ToLowerInvariant()}:{input.Amount}");
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoice;
    }

    /// <summary>Hóa đơn tổng 0 (toàn bộ dùng buổi gói) → chốt luôn không cần thu tiền.</summary>
    public async Task<Invoice> CloseZeroAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        EnsureOpen(invoice);
        if (invoice.Total != 0)
        {
            throw new ConflictException("Hóa đơn còn tiền phải thu.", "invoice_state");
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = time.GetUtcNow();
        await SettleAsync(tenantId, invoice, cancellationToken);
        Audit(tenantId, "invoice.closed_zero", invoice.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoice;
    }

    /// <summary>Hủy hóa đơn (quyền void_invoice): hoàn buổi gói đã trừ, thu hồi gói mới mua chưa dùng, hoàn ví, doanh thu âm.</summary>
    public async Task<Invoice> VoidAsync(Guid tenantId, Guid invoiceId, string reason, CancellationToken cancellationToken)
    {
        if (!_caller.Can(TenantPermissions.VoidInvoice))
        {
            throw new ForbiddenException();
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidInputException("Cần ghi lý do hủy.");
        }

        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        if (invoice.Status == InvoiceStatus.Void)
        {
            return invoice;
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Partial or InvoiceStatus.Refunded)
        {
            await UnsettleAsync(tenantId, invoice, cancellationToken);
            var walletPaid = await Db.Payments.Where(p => p.TenantId == tenantId && p.InvoiceId == invoice.Id && p.Method == PaymentMethod.Wallet).SumAsync(p => p.Amount, cancellationToken);
            if (walletPaid > 0 && invoice.ContactId is { } contactId)
            {
                await wallets.RefundAsync(tenantId, contactId, walletPaid, invoice.Id, _caller.UserId, cancellationToken);
            }
        }

        if (invoice.VoucherId is { } voucherId)
        {
            await Db.Vouchers.Where(v => v.TenantId == tenantId && v.Id == voucherId && v.UsedCount > 0).ExecuteUpdateAsync(s => s.SetProperty(v => v.UsedCount, v => v.UsedCount - 1), cancellationToken);
        }

        invoice.Status = InvoiceStatus.Void;
        invoice.VoidedAt = now;
        invoice.VoidedBy = _caller.UserId;
        invoice.VoidReason = reason.Trim();
        Audit(tenantId, "invoice.voided", invoice.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoice;
    }

    /// <summary>Hoàn tiền một phần/toàn bộ (quyền refund): payment âm + doanh thu âm; không đụng gói/buổi (hủy hóa đơn nếu muốn thu hồi).</summary>
    public async Task<Invoice> RefundAsync(Guid tenantId, Guid invoiceId, long amount, PaymentMethod method, string reason, CancellationToken cancellationToken)
    {
        if (!_caller.Can(TenantPermissions.Refund))
        {
            throw new ForbiddenException();
        }

        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        if (invoice.Status is not (InvoiceStatus.Paid or InvoiceStatus.Partial or InvoiceStatus.Refunded))
        {
            throw new ConflictException("Chỉ hoàn tiền hóa đơn đã thu.", "invoice_state");
        }

        if (amount <= 0 || amount > invoice.PaidAmount - invoice.RefundedAmount || string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidInputException("Số tiền hoàn phải > 0, không vượt số đã thu, và có lý do.");
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (method == PaymentMethod.Wallet && invoice.ContactId is { } contactId)
        {
            await wallets.RefundAsync(tenantId, contactId, amount, invoice.Id, _caller.UserId, cancellationToken);
        }

        Db.Payments.Add(new Payment
        {
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            Method = PaymentMethod.Refund,
            Amount = -amount,
            Reference = $"{method.ToString().ToLowerInvariant()}: {reason.Trim()}",
            ReceivedByUserId = _caller.UserId,
            CashShiftId = method == PaymentMethod.Cash ? await OpenShiftIdAsync(tenantId, invoice.BranchId, cancellationToken) : null,
            ReceivedAt = now,
        });
        invoice.RefundedAmount += amount;
        invoice.Status = InvoiceStatus.Refunded;
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        Db.RevenueEntries.Add(new RevenueEntry
        {
            TenantId = tenantId,
            BranchId = invoice.BranchId,
            Date = CrmClock.LocalDate(now, CrmClock.Zone(settings.TimeZone)),
            Kind = RevenueKind.Refund,
            Amount = -amount,
            InvoiceId = invoice.Id,
            ContactId = invoice.ContactId,
            Source = await SourceAsync(tenantId, invoice, cancellationToken),
            At = now,
        });
        Audit(tenantId, "invoice.refunded", $"{invoice.Id}:{amount}");
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return invoice;
    }

    public async Task MarkReceiptSentAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: true, cancellationToken);
        invoice.ReceiptSentAt = time.GetUtcNow();
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Chuỗi VietQR + nội dung cho số tiền còn phải thu của hóa đơn (tài khoản mặc định của chi nhánh, hoặc của DN).</summary>
    public async Task<(string Payload, string BankName, string AccountNo, string AccountName, long Amount, string Purpose)?> TransferQrAsync(Guid tenantId, Guid invoiceId, long? amount, CancellationToken cancellationToken)
    {
        var invoice = await RequireAsync(tenantId, invoiceId, tracked: false, cancellationToken);
        var account = await Db.TenantBankAccounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId && (a.BranchId == invoice.BranchId || a.BranchId == null))
            .OrderByDescending(a => a.BranchId == invoice.BranchId).ThenByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return null;
        }

        var pay = Math.Clamp(amount ?? invoice.Due, 0, Math.Max(invoice.Due, 0));
        var accountNo = encryptor.Decrypt(account.AccountNoEnc);
        var purpose = VietQr.CleanPurpose($"HD{invoice.Number:000000}");
        return (VietQr.Build(account.BankBin, accountNo, pay > 0 ? pay : null, purpose), account.BankName, accountNo, account.AccountName, pay, purpose);
    }

    // ---- nội bộ ----

    internal async Task<Invoice> RequireAsync(Guid tenantId, Guid invoiceId, bool tracked, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = tracked ? Db.Invoices : Db.Invoices.AsNoTracking();
        var invoice = await query.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Id == invoiceId, cancellationToken) ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && invoice.BranchId != only)
        {
            throw new NotFoundException();
        }

        return invoice;
    }

    internal async Task<InvoiceView> ViewAsync(Guid tenantId, Invoice invoice, CancellationToken cancellationToken)
    {
        var lines = await Db.InvoiceLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.InvoiceId == invoice.Id).OrderBy(l => l.Position).ToListAsync(cancellationToken);
        var payments = await Db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId && p.InvoiceId == invoice.Id).OrderBy(p => p.ReceivedAt).ToListAsync(cancellationToken);
        var contact = invoice.ContactId is { } cid ? await Db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == cid, cancellationToken) : null;
        var branchName = await Db.Branches.AsNoTracking().Where(b => b.Id == invoice.BranchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
        var voucherCode = invoice.VoucherId is { } vid ? await Db.Vouchers.AsNoTracking().Where(v => v.Id == vid).Select(v => v.Code).FirstOrDefaultAsync(cancellationToken) : null;
        return new InvoiceView(
            invoice,
            lines,
            payments,
            contact is null ? null : ContactView.Name(contact, ContactView.LeadFields(contact, encryptor)),
            contact?.PhoneEnc is null ? null : PhoneNumber.Mask(encryptor.Decrypt(contact.PhoneEnc)),
            branchName,
            invoice.NoteEnc is null ? null : encryptor.Decrypt(invoice.NoteEnc),
            voucherCode);
    }

    private async Task ReplaceLinesAsync(Guid tenantId, Invoice invoice, IReadOnlyList<InvoiceLineInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new InvalidInputException("Hóa đơn cần ít nhất một dòng.");
        }

        var itemIds = inputs.Where(l => l.CatalogItemId != null).Select(l => l.CatalogItemId!.Value).Distinct().ToList();
        var items = await Db.CatalogItems.AsNoTracking().Where(c => c.TenantId == tenantId && itemIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var packageIds = inputs.Where(l => l.CustomerPackageId != null).Select(l => l.CustomerPackageId!.Value).Distinct().ToList();
        var customerPackages = await Db.CustomerPackages.AsNoTracking().Where(p => p.TenantId == tenantId && packageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        long subtotal = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input.Quantity <= 0 || input.Discount < 0)
            {
                throw new InvalidInputException("Số lượng phải > 0, giảm giá dòng không âm.");
            }

            var line = new InvoiceLine { TenantId = tenantId, InvoiceId = invoice.Id, Description = "", Position = i, Quantity = input.Quantity, PerformerStaffId = input.PerformerStaffId };
            if (input.CustomerPackageId is { } pid)
            {
                var package = customerPackages.GetValueOrDefault(pid) ?? throw new NotFoundException();
                if (invoice.ContactId != package.ContactId || package.Status != CustomerPackageStatus.Active)
                {
                    throw new ConflictException("Gói không thuộc khách này hoặc không còn hiệu lực.", "package_unavailable");
                }

                var pending = inputs.Take(i).Count(l => l.CustomerPackageId == pid) + 1;
                if (pending > package.Remaining)
                {
                    throw new ConflictException($"Gói {package.Name} chỉ còn {package.Remaining} buổi.", "package_exhausted");
                }

                line.Kind = InvoiceLineKind.Redemption;
                line.CustomerPackageId = pid;
                line.CatalogItemId = input.CatalogItemId;
                line.Description = string.IsNullOrWhiteSpace(input.Description) ? $"Dùng buổi gói: {package.Name}" : input.Description.Trim();
                line.Quantity = 1;
                line.UnitPrice = 0;
                line.LineTotal = 0;
            }
            else if (input.CatalogItemId is { } cid)
            {
                var item = items.GetValueOrDefault(cid) ?? throw new NotFoundException();
                line.Kind = item.Kind switch { CatalogKind.Service => InvoiceLineKind.Service, CatalogKind.Package => InvoiceLineKind.Package, _ => InvoiceLineKind.Product };
                line.CatalogItemId = cid;
                line.Description = string.IsNullOrWhiteSpace(input.Description) ? item.Name : input.Description.Trim();
                line.UnitPrice = input.UnitPrice ?? item.Price ?? throw new InvalidInputException($"{item.Name} chưa có giá — nhập giá cho dòng này.");
                line.Discount = input.Discount;
                line.LineTotal = Math.Max(0, line.UnitPrice * line.Quantity - line.Discount);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(input.Description) || input.UnitPrice is null)
                {
                    throw new InvalidInputException("Dòng tự nhập cần mô tả và đơn giá.");
                }

                line.Kind = InvoiceLineKind.Custom;
                line.Description = input.Description.Trim();
                line.UnitPrice = input.UnitPrice.Value;
                line.Discount = input.Discount;
                line.LineTotal = Math.Max(0, line.UnitPrice * line.Quantity - line.Discount);
            }

            if (line.UnitPrice < 0)
            {
                throw new InvalidInputException("Đơn giá không âm.");
            }

            subtotal += line.LineTotal;
            Db.InvoiceLines.Add(line);
        }

        invoice.Subtotal = subtotal;
        invoice.DiscountAmount = Math.Min(invoice.DiscountAmount, subtotal);
        Recalculate(invoice);
    }

    private static void Recalculate(Invoice invoice) =>
        invoice.Total = Math.Max(0, invoice.Subtotal - invoice.DiscountAmount - invoice.VoucherAmount);

    /// <summary>Hóa đơn vừa thu đủ: doanh thu dịch vụ/sản phẩm/tự nhập; tạo gói cho khách; trừ buổi gói + doanh thu gói; voucher tính lượt.</summary>
    private async Task SettleAsync(Guid tenantId, Invoice invoice, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var zone = CrmClock.Zone(settings.TimeZone);
        var now = time.GetUtcNow();
        var date = CrmClock.LocalDate(now, zone);
        var source = await SourceAsync(tenantId, invoice, cancellationToken);
        var lines = await Db.InvoiceLines.Where(l => l.TenantId == tenantId && l.InvoiceId == invoice.Id).OrderBy(l => l.Position).ToListAsync(cancellationToken);

        // Giảm giá hóa đơn/voucher chia theo tỷ lệ cho các dòng có tiền để doanh thu khớp tổng thu.
        var totalDiscount = invoice.DiscountAmount + invoice.VoucherAmount;
        var priced = lines.Where(l => l.LineTotal > 0).ToList();
        var allocated = 0L;
        for (var i = 0; i < priced.Count; i++)
        {
            var line = priced[i];
            var share = i == priced.Count - 1 ? totalDiscount - allocated : (long)Math.Round((double)totalDiscount * line.LineTotal / Math.Max(1, invoice.Subtotal));
            allocated += share;
            var net = line.LineTotal - share;
            if (line.Kind == InvoiceLineKind.Package && line.CatalogItemId is { } packageItemId)
            {
                var item = await Db.CatalogItems.AsNoTracking().FirstAsync(c => c.Id == packageItemId, cancellationToken);
                for (var q = 0; q < line.Quantity; q++)
                {
                    if (invoice.ContactId is { } contactId)
                    {
                        await packages.GrantAsync(tenantId, contactId, item, invoice.Id, net / line.Quantity, cancellationToken);
                    }
                }

                continue; // doanh thu gói ghi nhận theo buổi dùng
            }

            Db.RevenueEntries.Add(new RevenueEntry
            {
                TenantId = tenantId,
                BranchId = invoice.BranchId,
                Date = date,
                Kind = line.Kind switch { InvoiceLineKind.Service => RevenueKind.Service, InvoiceLineKind.Product => RevenueKind.Product, _ => RevenueKind.Custom },
                Amount = net,
                InvoiceId = invoice.Id,
                InvoiceLineId = line.Id,
                CatalogItemId = line.CatalogItemId,
                StaffMemberId = line.PerformerStaffId,
                ContactId = invoice.ContactId,
                Source = source,
                At = now,
            });
        }

        foreach (var line in lines.Where(l => l.Kind == InvoiceLineKind.Redemption && l.CustomerPackageId is not null))
        {
            await packages.RedeemAsync(tenantId, line.CustomerPackageId!.Value, invoice.AppointmentId, line.Id, line.PerformerStaffId, invoice.BranchId, source, cancellationToken);
        }

        if (invoice.VoucherId is { } voucherId)
        {
            await Db.Vouchers.Where(v => v.TenantId == tenantId && v.Id == voucherId).ExecuteUpdateAsync(s => s.SetProperty(v => v.UsedCount, v => v.UsedCount + 1), cancellationToken);
        }
    }

    private async Task UnsettleAsync(Guid tenantId, Invoice invoice, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var date = CrmClock.LocalDate(now, CrmClock.Zone(settings.TimeZone));
        var entries = await Db.RevenueEntries.AsNoTracking().Where(r => r.TenantId == tenantId && r.InvoiceId == invoice.Id && r.Kind != RevenueKind.Refund).ToListAsync(cancellationToken);
        foreach (var e in entries)
        {
            Db.RevenueEntries.Add(new RevenueEntry
            {
                TenantId = tenantId,
                BranchId = e.BranchId,
                Date = date,
                Kind = RevenueKind.Refund,
                Amount = -e.Amount,
                InvoiceId = invoice.Id,
                InvoiceLineId = e.InvoiceLineId,
                CatalogItemId = e.CatalogItemId,
                StaffMemberId = e.StaffMemberId,
                ContactId = e.ContactId,
                Source = e.Source,
                At = now,
            });
        }

        await packages.RevertForInvoiceAsync(tenantId, invoice.Id, cancellationToken);
    }

    private async Task<string?> SourceAsync(Guid tenantId, Invoice invoice, CancellationToken cancellationToken)
    {
        if (invoice.AppointmentId is { } aid)
        {
            var source = await Db.Appointments.AsNoTracking().Where(a => a.TenantId == tenantId && a.Id == aid).Select(a => (AppointmentSource?)a.Source).FirstOrDefaultAsync(cancellationToken);
            return source switch { AppointmentSource.Bot => "bot", AppointmentSource.Online => "online", AppointmentSource.WalkIn => "walk_in", AppointmentSource.Staff => "staff", _ => null };
        }

        return "walk_in";
    }

    private async Task<Guid?> OpenShiftIdAsync(Guid tenantId, Guid branchId, CancellationToken cancellationToken) =>
        await Db.CashShifts.Where(s => s.TenantId == tenantId && s.BranchId == branchId && s.ClosedAt == null).OrderByDescending(s => s.OpenedAt).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);

    private async Task<int> NextNumberAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"invoice:{tenantId:N}")), 0);
        await Db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        var max = await Db.Invoices.Where(i => i.TenantId == tenantId).MaxAsync(i => (int?)i.Number, cancellationToken) ?? 0;
        return max + 1;
    }

    private static void EnsureOpen(Invoice invoice)
    {
        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new ConflictException("Hóa đơn đã thu tiền, không sửa được (hủy rồi tạo lại).", "invoice_state");
        }
    }

    private static CustomerPackage Clone(CustomerPackage p, int deltaRemaining) => new()
    {
        Id = p.Id,
        TenantId = p.TenantId,
        ContactId = p.ContactId,
        CatalogItemId = p.CatalogItemId,
        Name = p.Name,
        TotalSessions = p.TotalSessions,
        UsedSessions = p.UsedSessions - deltaRemaining,
        Status = p.Status,
        ExpiresAt = p.ExpiresAt,
        PricePaid = p.PricePaid,
    };

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });

    public static string Number(int number) => $"HD-{number.ToString("000000", CultureInfo.InvariantCulture)}";
}
