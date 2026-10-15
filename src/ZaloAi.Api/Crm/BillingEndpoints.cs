using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Billing;

namespace ZaloAi.Api.Crm;

/// <param name="Kind">service | package | product | redemption | custom</param>
public sealed record InvoiceLineResponse(Guid Id, string Kind, Guid? CatalogItemId, string Description, int Quantity, long UnitPrice, long Discount, long LineTotal, Guid? PerformerStaffId, Guid? CustomerPackageId);

/// <param name="Method">cash | transfer | card | wallet | refund</param>
public sealed record PaymentResponse(Guid Id, string Method, long Amount, string? Reference, DateTimeOffset ReceivedAt, Guid? ReceivedByUserId);

/// <param name="Status">open | partial | paid | void | refunded</param>
public sealed record InvoiceResponse(
    Guid Id,
    string Number,
    Guid BranchId,
    string BranchName,
    Guid? ContactId,
    string? ContactName,
    string? ContactPhone,
    Guid? AppointmentId,
    string Status,
    long Subtotal,
    long DiscountAmount,
    string? DiscountReason,
    string? VoucherCode,
    long VoucherAmount,
    long Total,
    long PaidAmount,
    long RefundedAmount,
    long Due,
    string? Note,
    DateTimeOffset IssuedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    DateTimeOffset? ReceiptSentAt,
    IReadOnlyList<InvoiceLineResponse> Lines,
    IReadOnlyList<PaymentResponse> Payments)
{
    internal static InvoiceResponse From(InvoiceView v)
    {
        var i = v.Invoice;
        return new InvoiceResponse(
            i.Id, InvoiceService.Number(i.Number), i.BranchId, v.BranchName, i.ContactId, v.ContactName, v.ContactPhone, i.AppointmentId,
            i.Status.ToString().ToLowerInvariant(), i.Subtotal, i.DiscountAmount, i.DiscountReason, v.VoucherCode, i.VoucherAmount, i.Total, i.PaidAmount, i.RefundedAmount, i.Due,
            v.Note, i.IssuedAt, i.PaidAt, i.VoidedAt, i.VoidReason, i.ReceiptSentAt,
            v.Lines.Select(l => new InvoiceLineResponse(l.Id, l.Kind.ToString().ToLowerInvariant(), l.CatalogItemId, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.LineTotal, l.PerformerStaffId, l.CustomerPackageId)).ToList(),
            v.Payments.Select(p => new PaymentResponse(p.Id, p.Method.ToString().ToLowerInvariant(), p.Amount, p.Reference, p.ReceivedAt, p.ReceivedByUserId)).ToList());
    }
}

public sealed record InvoiceSummaryResponse(Guid Id, string Number, Guid BranchId, string BranchName, Guid? ContactId, string? ContactName, string Status, long Total, long PaidAmount, long Due, int LineCount, DateTimeOffset IssuedAt, DateTimeOffset? PaidAt)
{
    internal static InvoiceSummaryResponse From(InvoiceSummary s) => new(
        s.Invoice.Id, InvoiceService.Number(s.Invoice.Number), s.Invoice.BranchId, s.BranchName, s.Invoice.ContactId, s.ContactName,
        s.Invoice.Status.ToString().ToLowerInvariant(), s.Invoice.Total, s.Invoice.PaidAmount, s.Invoice.Due, s.LineCount, s.Invoice.IssuedAt, s.Invoice.PaidAt);
}

/// <param name="CatalogItemId">Mục danh mục; null = dòng tự nhập (cần Description + UnitPrice) hoặc dòng dùng buổi gói (CustomerPackageId).</param>
/// <param name="UnitPrice">null = lấy giá danh mục.</param>
public sealed record InvoiceLineRequest(Guid? CatalogItemId, Guid? CustomerPackageId, string? Description, int Quantity = 1, long? UnitPrice = null, long Discount = 0, Guid? PerformerStaffId = null);

public sealed record CreateInvoiceRequest(Guid? BranchId, Guid? ContactId, Guid? AppointmentId, IReadOnlyList<InvoiceLineRequest> Lines, string? Note);

public sealed record UpdateInvoiceLinesRequest(IReadOnlyList<InvoiceLineRequest> Lines, string? Note);

public sealed record ApplyDiscountRequest(long Amount, string? Reason, string? VoucherCode);

/// <param name="Method">cash | transfer | card | wallet</param>
public sealed record AddPaymentRequest(string Method, long Amount, string? Reference);

public sealed record VoidInvoiceRequest(string Reason);

/// <param name="Method">cash | transfer | wallet</param>
public sealed record RefundInvoiceRequest(long Amount, string Method, string Reason);

public sealed record TransferQrResponse(string Payload, string BankName, string AccountNo, string AccountName, long Amount, string Purpose, string PngBase64);

/// <param name="Status">active | paused | expired | exhausted | transferred | cancelled</param>
public sealed record CustomerPackageResponse(Guid Id, Guid ContactId, Guid CatalogItemId, string Name, int TotalSessions, int UsedSessions, int Remaining, long PricePaid, DateTimeOffset? ExpiresAt, string Status, Guid? InvoiceId, DateTimeOffset CreatedAt)
{
    internal static CustomerPackageResponse From(CustomerPackage p) =>
        new(p.Id, p.ContactId, p.CatalogItemId, p.Name, p.TotalSessions, p.UsedSessions, p.Remaining, p.PricePaid, p.ExpiresAt, p.Status.ToString().ToLowerInvariant(), p.InvoiceId, p.CreatedAt);
}

public sealed record PackageRedemptionResponse(Guid Id, Guid? AppointmentId, Guid? StaffMemberId, DateTimeOffset At, DateTimeOffset? RevertedAt);

/// <param name="Status">active | paused | cancelled</param>
public sealed record SetPackageStatusRequest(string Status);

public sealed record AdjustPackageRequest(int? TotalSessions, DateTimeOffset? ExpiresAt);

public sealed record TransferPackageRequest(Guid ToContactId);

/// <param name="Kind">topup | bonus | spend | refund | adjust</param>
public sealed record WalletTransactionResponse(Guid Id, string Kind, long Amount, long BalanceAfter, Guid? InvoiceId, string? Note, DateTimeOffset At);

public sealed record WalletResponse(long Balance, IReadOnlyList<WalletTransactionResponse> Transactions);

public sealed record WalletTopupRequest(long Amount, long Bonus = 0, string? Note = null);

public sealed record WalletAdjustRequest(long Delta, string Reason);

/// <param name="Kind">percent | amount</param>
public sealed record VoucherResponse(Guid Id, string Code, string Kind, long Value, long? MaxDiscount, long MinTotal, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, int? MaxUses, int UsedCount, bool IsActive, string? Note)
{
    internal static VoucherResponse From(Voucher v) => new(v.Id, v.Code, v.Kind.ToString().ToLowerInvariant(), v.Value, v.MaxDiscount, v.MinTotal, v.ValidFrom, v.ValidTo, v.MaxUses, v.UsedCount, v.IsActive, v.Note);
}

/// <param name="Kind">percent | amount</param>
public sealed record SaveVoucherRequest(string Code, string Kind, long Value, long? MaxDiscount, long MinTotal, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, int? MaxUses, bool IsActive = true, string? Note = null);

public sealed record CashShiftResponse(Guid Id, Guid BranchId, DateTimeOffset OpenedAt, long OpeningCash, DateTimeOffset? ClosedAt, long? ExpectedCash, long? CountedCash, string? Note, long CashIn, long CashOut, long Transfer, long Card, long Wallet, int InvoiceCount)
{
    internal static CashShiftResponse From(CashShiftSummary s) => new(
        s.Shift.Id, s.Shift.BranchId, s.Shift.OpenedAt, s.Shift.OpeningCash, s.Shift.ClosedAt, s.Shift.ExpectedCash, s.Shift.CountedCash, s.Shift.Note,
        s.CashIn, s.CashOut, s.Transfer, s.Card, s.Wallet, s.InvoiceCount);
}

public sealed record OpenCashShiftRequest(Guid BranchId, long OpeningCash);

public sealed record CloseCashShiftRequest(long CountedCash, string? Note);

public sealed record BankAccountResponse(Guid Id, Guid? BranchId, string BankBin, string BankName, string AccountNoMasked, string AccountName, bool IsDefault)
{
    internal static BankAccountResponse From(BankAccountView v) => new(v.Account.Id, v.Account.BranchId, v.Account.BankBin, v.Account.BankName, v.AccountNoMasked, v.Account.AccountName, v.Account.IsDefault);
}

public sealed record SaveBankAccountRequest(Guid? BranchId, string BankBin, string BankName, string AccountNo, string AccountName, bool IsDefault = false);

public sealed record ReceiptResponse(string Text, bool Sent);

internal sealed class InvoiceLineRequestValidator : AbstractValidator<InvoiceLineRequest>
{
    public InvoiceLineRequestValidator()
    {
        RuleFor(x => x.Quantity).InclusiveBetween(1, 1000);
        RuleFor(x => x.Discount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0).When(x => x.UnitPrice is not null);
        RuleFor(x => x.Description).MaximumLength(300);
    }
}

internal sealed class CreateInvoiceRequestValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceRequestValidator()
    {
        RuleFor(x => x.Lines).NotNull().Must(l => l.Count is >= 1 and <= 100).WithMessage("Hóa đơn 1–100 dòng.");
        RuleForEach(x => x.Lines).SetValidator(new InvoiceLineRequestValidator());
        RuleFor(x => x.Note).MaximumLength(2000);
    }
}

internal sealed class UpdateInvoiceLinesRequestValidator : AbstractValidator<UpdateInvoiceLinesRequest>
{
    public UpdateInvoiceLinesRequestValidator()
    {
        RuleFor(x => x.Lines).NotNull().Must(l => l.Count is >= 1 and <= 100).WithMessage("Hóa đơn 1–100 dòng.");
        RuleForEach(x => x.Lines).SetValidator(new InvoiceLineRequestValidator());
        RuleFor(x => x.Note).MaximumLength(2000);
    }
}

internal sealed class ApplyDiscountRequestValidator : AbstractValidator<ApplyDiscountRequest>
{
    public ApplyDiscountRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Reason).MaximumLength(500);
        RuleFor(x => x.VoucherCode).MaximumLength(50);
    }
}

internal sealed class AddPaymentRequestValidator : AbstractValidator<AddPaymentRequest>
{
    public AddPaymentRequestValidator()
    {
        RuleFor(x => x.Method).Must(m => m is "cash" or "transfer" or "card" or "wallet").WithMessage("Hình thức: cash, transfer, card, wallet.");
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Reference).MaximumLength(100);
    }
}

internal sealed class VoidInvoiceRequestValidator : AbstractValidator<VoidInvoiceRequest>
{
    public VoidInvoiceRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(500).WithName("Lý do");
}

internal sealed class RefundInvoiceRequestValidator : AbstractValidator<RefundInvoiceRequest>
{
    public RefundInvoiceRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Method).Must(m => m is "cash" or "transfer" or "wallet");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500).WithName("Lý do");
    }
}

internal sealed class SetPackageStatusRequestValidator : AbstractValidator<SetPackageStatusRequest>
{
    public SetPackageStatusRequestValidator() => RuleFor(x => x.Status).Must(s => s is "active" or "paused" or "cancelled");
}

internal sealed class WalletTopupRequestValidator : AbstractValidator<WalletTopupRequest>
{
    public WalletTopupRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Bonus).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

internal sealed class WalletAdjustRequestValidator : AbstractValidator<WalletAdjustRequest>
{
    public WalletAdjustRequestValidator()
    {
        RuleFor(x => x.Delta).NotEqual(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300).WithName("Lý do");
    }
}

internal sealed class SaveVoucherRequestValidator : AbstractValidator<SaveVoucherRequest>
{
    public SaveVoucherRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50).WithName("Mã");
        RuleFor(x => x.Kind).Must(k => k is "percent" or "amount");
        RuleFor(x => x.Value).GreaterThan(0);
        RuleFor(x => x.MinTotal).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

internal sealed class OpenCashShiftRequestValidator : AbstractValidator<OpenCashShiftRequest>
{
    public OpenCashShiftRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.OpeningCash).GreaterThanOrEqualTo(0);
    }
}

internal sealed class CloseCashShiftRequestValidator : AbstractValidator<CloseCashShiftRequest>
{
    public CloseCashShiftRequestValidator()
    {
        RuleFor(x => x.CountedCash).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class SaveBankAccountRequestValidator : AbstractValidator<SaveBankAccountRequest>
{
    public SaveBankAccountRequestValidator()
    {
        RuleFor(x => x.BankBin).NotEmpty().MaximumLength(10);
        RuleFor(x => x.BankName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.AccountNo).NotEmpty().MaximumLength(40);
        RuleFor(x => x.AccountName).NotEmpty().MaximumLength(100);
    }
}

/// <summary>Bán hàng (CRM C3): hóa đơn, thanh toán, VietQR, phiếu thu, gói của khách, ví, voucher, ca thu ngân, tài khoản ngân hàng.</summary>
internal static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var invoices = app.MapGroup("/invoices").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        invoices.MapGet("", ListInvoicesAsync);
        invoices.MapGet("/{invoiceId:guid}", GetInvoiceAsync).ProducesProblem(StatusCodes.Status404NotFound);
        invoices.MapPost("", CreateInvoiceAsync).Validate<CreateInvoiceRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        invoices.MapPost("/from-appointment/{appointmentId:guid}", CreateFromAppointmentAsync).ProducesProblem(StatusCodes.Status404NotFound);
        invoices.MapPut("/{invoiceId:guid}/lines", UpdateLinesAsync).Validate<UpdateInvoiceLinesRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        invoices.MapPost("/{invoiceId:guid}/discount", ApplyDiscountAsync).Validate<ApplyDiscountRequest>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict);
        invoices.MapPost("/{invoiceId:guid}/payments", AddPaymentAsync).Validate<AddPaymentRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        invoices.MapPost("/{invoiceId:guid}/close-zero", CloseZeroAsync).ProducesProblem(StatusCodes.Status409Conflict);
        invoices.MapPost("/{invoiceId:guid}/void", VoidAsync).Validate<VoidInvoiceRequest>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict);
        invoices.MapPost("/{invoiceId:guid}/refund", RefundAsync).Validate<RefundInvoiceRequest>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict);
        invoices.MapGet("/{invoiceId:guid}/transfer-qr", TransferQrAsync).ProducesProblem(StatusCodes.Status404NotFound);
        invoices.MapGet("/{invoiceId:guid}/receipt", ReceiptTextAsync);
        invoices.MapPost("/{invoiceId:guid}/receipt/send", SendReceiptAsync).ProducesProblem(StatusCodes.Status409Conflict);

        var packages = app.MapGroup("/customer-packages").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        packages.MapGet("", ListPackagesAsync);
        packages.MapGet("/{packageId:guid}/redemptions", RedemptionsAsync);
        packages.MapPatch("/{packageId:guid}/status", SetPackageStatusAsync).Validate<SetPackageStatusRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        packages.MapPatch("/{packageId:guid}", AdjustPackageAsync).RequirePermission(TenantPermissions.ManageCatalog);
        packages.MapPost("/{packageId:guid}/transfer", TransferPackageAsync).RequirePermission(TenantPermissions.ManageCatalog).ProducesProblem(StatusCodes.Status409Conflict);

        var wallets = app.MapGroup("/contacts/{contactId:guid}/wallet").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        wallets.MapGet("", GetWalletAsync);
        wallets.MapPost("/topup", TopupAsync).Validate<WalletTopupRequest>();
        wallets.MapPost("/adjust", AdjustWalletAsync).RequirePermission(TenantPermissions.Refund).Validate<WalletAdjustRequest>().ProducesProblem(StatusCodes.Status409Conflict);

        var vouchers = app.MapGroup("/vouchers").WithTags("CRM");
        vouchers.MapGet("", ListVouchersAsync).RequireTenantRole(TenantRole.Staff);
        vouchers.MapPost("", CreateVoucherAsync).RequirePermission(TenantPermissions.ManageCatalog).Validate<SaveVoucherRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        vouchers.MapPut("/{voucherId:guid}", UpdateVoucherAsync).RequirePermission(TenantPermissions.ManageCatalog).Validate<SaveVoucherRequest>().ProducesProblem(StatusCodes.Status409Conflict);

        var shifts = app.MapGroup("/cash-shifts").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        shifts.MapGet("", ListShiftsAsync);
        shifts.MapGet("/current", CurrentShiftAsync);
        shifts.MapPost("", OpenShiftAsync).Validate<OpenCashShiftRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        shifts.MapPost("/{shiftId:guid}/close", CloseShiftAsync).Validate<CloseCashShiftRequest>().ProducesProblem(StatusCodes.Status409Conflict);

        var banks = app.MapGroup("/tenant/bank-accounts").WithTags("CRM");
        banks.MapGet("", ListBankAccountsAsync).RequireTenantRole(TenantRole.Staff);
        banks.MapPost("", CreateBankAccountAsync).RequireTenantRole(TenantRole.Owner).Validate<SaveBankAccountRequest>();
        banks.MapDelete("/{accountId:guid}", DeleteBankAccountAsync).RequireTenantRole(TenantRole.Owner).ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static InvoiceStatus? ParseStatus(string? status) => status switch
    {
        "open" => InvoiceStatus.Open,
        "partial" => InvoiceStatus.Partial,
        "paid" => InvoiceStatus.Paid,
        "void" => InvoiceStatus.Void,
        "refunded" => InvoiceStatus.Refunded,
        _ => null,
    };

    private static PaymentMethod ParseMethod(string method) => method switch
    {
        "transfer" => PaymentMethod.Transfer,
        "card" => PaymentMethod.Card,
        "wallet" => PaymentMethod.Wallet,
        _ => PaymentMethod.Cash,
    };

    private static InvoiceLineInput ToInput(InvoiceLineRequest l) => new(l.CatalogItemId, l.CustomerPackageId, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.PerformerStaffId);

    /// <param name="from">ISO 8601 (mặc định 30 ngày trước)</param>
    /// <param name="to">ISO 8601 (mặc định bây giờ)</param>
    private static async Task<Ok<List<InvoiceSummaryResponse>>> ListInvoicesAsync(
        DateTimeOffset? from, DateTimeOffset? to, Guid? branchId, string? status, Guid? contactId, ITenantContext tenant, InvoiceService invoices, TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var list = await invoices.ListAsync(tenant.RequireTenantId(), branchId, from ?? now.AddDays(-30), to ?? now.AddDays(1), ParseStatus(status), contactId, cancellationToken);
        return TypedResults.Ok(list.Select(InvoiceSummaryResponse.From).ToList());
    }

    private static async Task<Ok<InvoiceResponse>> GetInvoiceAsync(Guid invoiceId, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken) =>
        TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenant.RequireTenantId(), invoiceId, cancellationToken)));

    private static async Task<Ok<InvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var created = await invoices.CreateAsync(tenantId, new InvoiceInput(request.BranchId, request.ContactId, request.AppointmentId, request.Lines.Select(ToInput).ToList(), request.Note), cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, created.Id, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> CreateFromAppointmentAsync(Guid appointmentId, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var created = await invoices.CreateFromAppointmentAsync(tenantId, appointmentId, cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, created.Id, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> UpdateLinesAsync(Guid invoiceId, UpdateInvoiceLinesRequest request, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await invoices.UpdateLinesAsync(tenantId, invoiceId, request.Lines.Select(ToInput).ToList(), request.Note, cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, invoiceId, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> ApplyDiscountAsync(Guid invoiceId, ApplyDiscountRequest request, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await invoices.ApplyDiscountAsync(tenantId, invoiceId, new DiscountInput(request.Amount, request.Reason, request.VoucherCode), cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, invoiceId, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> AddPaymentAsync(Guid invoiceId, AddPaymentRequest request, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await invoices.AddPaymentAsync(tenantId, invoiceId, new PaymentInput(ParseMethod(request.Method), request.Amount, request.Reference), cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, invoiceId, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> CloseZeroAsync(Guid invoiceId, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await invoices.CloseZeroAsync(tenantId, invoiceId, cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, invoiceId, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await invoices.VoidAsync(tenantId, invoiceId, request.Reason, cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, invoiceId, cancellationToken)));
    }

    private static async Task<Ok<InvoiceResponse>> RefundAsync(Guid invoiceId, RefundInvoiceRequest request, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await invoices.RefundAsync(tenantId, invoiceId, request.Amount, ParseMethod(request.Method), request.Reason, cancellationToken);
        return TypedResults.Ok(InvoiceResponse.From(await invoices.GetAsync(tenantId, invoiceId, cancellationToken)));
    }

    /// <param name="amount">Số tiền muốn thu qua QR; mặc định = còn phải thu.</param>
    private static async Task<Results<Ok<TransferQrResponse>, ProblemHttpResult>> TransferQrAsync(Guid invoiceId, long? amount, ITenantContext tenant, InvoiceService invoices, CancellationToken cancellationToken)
    {
        var qr = await invoices.TransferQrAsync(tenant.RequireTenantId(), invoiceId, amount, cancellationToken);
        if (qr is not { } q)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Doanh nghiệp chưa cài tài khoản ngân hàng nhận chuyển khoản.",
                extensions: new Dictionary<string, object?> { ["code"] = "no_bank_account" });
        }

        return TypedResults.Ok(new TransferQrResponse(q.Payload, q.BankName, q.AccountNo, q.AccountName, q.Amount, q.Purpose, Convert.ToBase64String(VietQr.Png(q.Payload))));
    }

    private static async Task<Ok<ReceiptResponse>> ReceiptTextAsync(Guid invoiceId, ITenantContext tenant, ReceiptService receipts, CancellationToken cancellationToken) =>
        TypedResults.Ok(new ReceiptResponse(await receipts.TextAsync(tenant.RequireTenantId(), invoiceId, cancellationToken), false));

    private static async Task<Ok<ReceiptResponse>> SendReceiptAsync(Guid invoiceId, ITenantContext tenant, ReceiptService receipts, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var sent = await receipts.SendAsync(tenantId, invoiceId, cancellationToken);
        return TypedResults.Ok(new ReceiptResponse(await receipts.TextAsync(tenantId, invoiceId, cancellationToken), sent));
    }

    private static async Task<Results<Ok<List<CustomerPackageResponse>>, ProblemHttpResult>> ListPackagesAsync(Guid? contactId, ITenantContext tenant, PackageService packages, CancellationToken cancellationToken)
    {
        if (contactId is not { } c)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Cần contactId.", extensions: new Dictionary<string, object?> { ["code"] = "invalid_input" });
        }

        return TypedResults.Ok((await packages.ListForContactAsync(tenant.RequireTenantId(), c, cancellationToken)).Select(CustomerPackageResponse.From).ToList());
    }

    private static async Task<Ok<List<PackageRedemptionResponse>>> RedemptionsAsync(Guid packageId, ITenantContext tenant, PackageService packages, CancellationToken cancellationToken) =>
        TypedResults.Ok((await packages.RedemptionsAsync(tenant.RequireTenantId(), packageId, cancellationToken)).Select(r => new PackageRedemptionResponse(r.Id, r.AppointmentId, r.StaffMemberId, r.At, r.RevertedAt)).ToList());

    private static async Task<Ok<CustomerPackageResponse>> SetPackageStatusAsync(Guid packageId, SetPackageStatusRequest request, ITenantContext tenant, PackageService packages, CancellationToken cancellationToken)
    {
        var status = request.Status switch { "paused" => CustomerPackageStatus.Paused, "cancelled" => CustomerPackageStatus.Cancelled, _ => CustomerPackageStatus.Active };
        return TypedResults.Ok(CustomerPackageResponse.From(await packages.SetStatusAsync(tenant.RequireTenantId(), packageId, status, cancellationToken)));
    }

    private static async Task<Ok<CustomerPackageResponse>> AdjustPackageAsync(Guid packageId, AdjustPackageRequest request, ITenantContext tenant, PackageService packages, CancellationToken cancellationToken) =>
        TypedResults.Ok(CustomerPackageResponse.From(await packages.AdjustAsync(tenant.RequireTenantId(), packageId, request.TotalSessions, request.ExpiresAt, cancellationToken)));

    private static async Task<Ok<CustomerPackageResponse>> TransferPackageAsync(Guid packageId, TransferPackageRequest request, ITenantContext tenant, PackageService packages, CancellationToken cancellationToken) =>
        TypedResults.Ok(CustomerPackageResponse.From(await packages.TransferAsync(tenant.RequireTenantId(), packageId, request.ToContactId, cancellationToken)));

    private static async Task<Ok<WalletResponse>> GetWalletAsync(Guid contactId, ITenantContext tenant, WalletService wallets, CancellationToken cancellationToken)
    {
        var (balance, transactions) = await wallets.GetAsync(tenant.RequireTenantId(), contactId, cancellationToken);
        return TypedResults.Ok(new WalletResponse(balance, transactions.Select(t => new WalletTransactionResponse(t.Id, t.Kind.ToString().ToLowerInvariant(), t.Amount, t.BalanceAfter, t.InvoiceId, t.Note, t.At)).ToList()));
    }

    private static async Task<Ok<WalletResponse>> TopupAsync(Guid contactId, WalletTopupRequest request, ITenantContext tenant, WalletService wallets, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await wallets.TopupAsync(tenantId, contactId, request.Amount, request.Bonus, request.Note, cancellationToken);
        return await GetWalletAsync(contactId, tenant, wallets, cancellationToken);
    }

    private static async Task<Ok<WalletResponse>> AdjustWalletAsync(Guid contactId, WalletAdjustRequest request, ITenantContext tenant, WalletService wallets, CancellationToken cancellationToken)
    {
        await wallets.AdjustAsync(tenant.RequireTenantId(), contactId, request.Delta, request.Reason, cancellationToken);
        return await GetWalletAsync(contactId, tenant, wallets, cancellationToken);
    }

    private static async Task<Ok<List<VoucherResponse>>> ListVouchersAsync(bool? includeInactive, ITenantContext tenant, VoucherService vouchers, CancellationToken cancellationToken) =>
        TypedResults.Ok((await vouchers.ListAsync(tenant.RequireTenantId(), includeInactive == true, cancellationToken)).Select(VoucherResponse.From).ToList());

    private static async Task<Ok<VoucherResponse>> CreateVoucherAsync(SaveVoucherRequest request, ITenantContext tenant, VoucherService vouchers, CancellationToken cancellationToken) =>
        TypedResults.Ok(VoucherResponse.From(await vouchers.CreateAsync(tenant.RequireTenantId(), ToInput(request), cancellationToken)));

    private static async Task<Ok<VoucherResponse>> UpdateVoucherAsync(Guid voucherId, SaveVoucherRequest request, ITenantContext tenant, VoucherService vouchers, CancellationToken cancellationToken) =>
        TypedResults.Ok(VoucherResponse.From(await vouchers.UpdateAsync(tenant.RequireTenantId(), voucherId, ToInput(request), cancellationToken)));

    private static VoucherInput ToInput(SaveVoucherRequest r) => new(r.Code, r.Kind == "percent" ? VoucherKind.Percent : VoucherKind.Amount, r.Value, r.MaxDiscount, r.MinTotal, r.ValidFrom, r.ValidTo, r.MaxUses, r.IsActive, r.Note);

    private static async Task<Ok<List<CashShiftResponse>>> ListShiftsAsync(Guid? branchId, int? limit, ITenantContext tenant, CashShiftService shifts, CancellationToken cancellationToken) =>
        TypedResults.Ok((await shifts.ListAsync(tenant.RequireTenantId(), branchId, limit ?? 30, cancellationToken)).Select(CashShiftResponse.From).ToList());

    private static async Task<Results<Ok<CashShiftResponse>, NoContent>> CurrentShiftAsync(Guid? branchId, ITenantContext tenant, CashShiftService shifts, ZaloAi.Infrastructure.Crm.BranchService branches, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var branch = branchId ?? (await branches.EnsureDefaultAsync(tenantId, cancellationToken)).Id;
        var current = await shifts.CurrentAsync(tenantId, branch, cancellationToken);
        return current is null ? TypedResults.NoContent() : TypedResults.Ok(CashShiftResponse.From(current));
    }

    private static async Task<Ok<CashShiftResponse>> OpenShiftAsync(OpenCashShiftRequest request, ITenantContext tenant, CashShiftService shifts, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var shift = await shifts.OpenAsync(tenantId, request.BranchId, request.OpeningCash, cancellationToken);
        return TypedResults.Ok(CashShiftResponse.From((await shifts.CurrentAsync(tenantId, shift.BranchId, cancellationToken))!));
    }

    private static async Task<Ok<CashShiftResponse>> CloseShiftAsync(Guid shiftId, CloseCashShiftRequest request, ITenantContext tenant, CashShiftService shifts, CancellationToken cancellationToken) =>
        TypedResults.Ok(CashShiftResponse.From(await shifts.CloseAsync(tenant.RequireTenantId(), shiftId, request.CountedCash, request.Note, cancellationToken)));

    private static async Task<Ok<List<BankAccountResponse>>> ListBankAccountsAsync(ITenantContext tenant, BankAccountService banks, CancellationToken cancellationToken) =>
        TypedResults.Ok((await banks.ListAsync(tenant.RequireTenantId(), cancellationToken)).Select(BankAccountResponse.From).ToList());

    private static async Task<Ok<BankAccountResponse>> CreateBankAccountAsync(SaveBankAccountRequest request, ITenantContext tenant, BankAccountService banks, CancellationToken cancellationToken) =>
        TypedResults.Ok(BankAccountResponse.From(await banks.CreateAsync(tenant.RequireTenantId(), new BankAccountInput(request.BranchId, request.BankBin, request.BankName, request.AccountNo, request.AccountName, request.IsDefault), cancellationToken)));

    private static async Task<NoContent> DeleteBankAccountAsync(Guid accountId, ITenantContext tenant, BankAccountService banks, CancellationToken cancellationToken)
    {
        await banks.DeleteAsync(tenant.RequireTenantId(), accountId, cancellationToken);
        return TypedResults.NoContent();
    }
}
