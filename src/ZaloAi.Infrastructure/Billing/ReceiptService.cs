using System.Globalization;
using System.Text;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>Phiếu thu dạng chữ gửi khách qua kênh chat (Zalo / chat thử) — CRM C3. Không gửi khi khách chưa có hội thoại hoặc quá hạn nhắn.</summary>
public sealed class ReceiptService(
    AppDbContext db,
    ITenantContext tenantContext,
    InvoiceService invoices,
    ContactRepository contacts,
    ConversationRepository conversations,
    TenantRepository tenants,
    CrmSettingsRepository settingsRepository,
    MessageDeliveryService delivery,
    TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public async Task<string> TextAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var view = await invoices.GetAsync(tenantId, invoiceId, cancellationToken);
        var tenant = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        return Build(view, tenant, CrmClock.Zone(settings.TimeZone));
    }

    /// <summary>Gửi phiếu thu cho khách; trả về false khi không có kênh nhắn được.</summary>
    public async Task<bool> SendAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var view = await invoices.GetAsync(tenantId, invoiceId, cancellationToken);
        if (view.Invoice.ContactId is not { } contactId || view.Invoice.Status is InvoiceStatus.Open or InvoiceStatus.Void)
        {
            throw new ConflictException("Chỉ gửi phiếu thu của hóa đơn đã thu tiền và có khách.", "invoice_state");
        }

        var contact = await contacts.GetAsync(tenantId, contactId, cancellationToken) ?? throw new NotFoundException();
        var conversation = await contacts.LatestConversationAsync(tenantId, contactId, cancellationToken);
        if (conversation is null || (CareDecision.MessagingDeadline(conversation) is { } deadline && deadline <= time.GetUtcNow()))
        {
            return false;
        }

        var tenant = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var message = delivery.AddOutgoing(tenantId, conversation, MessageSender.System, Build(view, tenant, CrmClock.Zone(settings.TimeZone)));
        await conversations.SaveChangesAsync(cancellationToken);
        await delivery.DeliverAsync(tenantId, conversation, contact, message, cancellationToken);
        await invoices.MarkReceiptSentAsync(tenantId, invoiceId, cancellationToken);
        return message.DeliveryStatus == DeliveryStatus.Sent;
    }

    public static string Build(InvoiceView view, Tenant tenant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(tenant);
        var i = view.Invoice;
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"PHIẾU THU {InvoiceService.Number(i.Number)} — {tenant.Name}");
        s.AppendLine(CultureInfo.InvariantCulture, $"Ngày: {CrmClock.ToLocal(i.PaidAt ?? i.IssuedAt, zone):dd/MM/yyyy HH:mm}");
        if (view.ContactName is not null)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Khách: {view.ContactName}");
        }

        foreach (var line in view.Lines)
        {
            var qty = line.Quantity > 1 ? $" x{line.Quantity}" : "";
            s.AppendLine(CultureInfo.InvariantCulture, $"- {line.Description}{qty}: {Money(line.LineTotal)}");
        }

        if (i.DiscountAmount > 0)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Giảm giá: -{Money(i.DiscountAmount)}");
        }

        if (i.VoucherAmount > 0)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Mã {view.VoucherCode}: -{Money(i.VoucherAmount)}");
        }

        s.AppendLine(CultureInfo.InvariantCulture, $"Tổng: {Money(i.Total)}");
        s.AppendLine(CultureInfo.InvariantCulture, $"Đã thu: {Money(i.PaidAmount - i.RefundedAmount)}{(i.Due > 0 ? $" (còn lại {Money(i.Due)})" : "")}");
        var methods = view.Payments.Where(p => p.Amount > 0).Select(p => Method(p.Method)).Distinct().ToList();
        if (methods.Count > 0)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"Hình thức: {string.Join(", ", methods)}");
        }

        s.Append(CultureInfo.InvariantCulture, $"{tenant.Name} cảm ơn {CustomerSalutation.Resolve(view.ContactPhone is null ? null : null)} đã tin tưởng 🙏");
        return s.ToString();
    }

    public static string Money(long amount) => amount.ToString("#,##0", Vietnamese) + " đ";

    private static string Method(PaymentMethod method) => method switch
    {
        PaymentMethod.Cash => "tiền mặt",
        PaymentMethod.Transfer => "chuyển khoản",
        PaymentMethod.Card => "thẻ",
        PaymentMethod.Wallet => "thẻ trả trước",
        _ => "khác",
    };
}
