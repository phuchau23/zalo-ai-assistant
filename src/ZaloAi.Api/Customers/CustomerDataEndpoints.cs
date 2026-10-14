using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.IndustryTemplates;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Customers;

/// <summary>
/// Quyền của khách cuối với dữ liệu cá nhân (CLAUDE.md mục 7, M6 đợt B2): xuất toàn bộ dữ liệu của một khách và xóa vĩnh viễn.
/// Chỉ owner; mọi thao tác ghi audit log (chỉ ghi mã khách, không ghi nội dung). Lọc tenant_id ở repository.
/// </summary>
internal static class CustomerDataEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapCustomerDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/contacts").WithTags("Customers");
        group.MapGet("/{contactId:guid}/export", ExportAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Produces(StatusCodes.Status200OK, contentType: XlsxContentType)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{contactId:guid}", DeleteAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    /// <summary>Excel 4 sheet: thông tin khách, tin nhắn (mọi hội thoại), nhật ký chăm sóc, gợi ý chăm sóc.</summary>
    private static async Task<FileContentHttpResult> ExportAsync(
        Guid contactId,
        ITenantContext tenant,
        ContactRepository contacts,
        TenantRepository tenants,
        MembershipRepository memberships,
        AuditLogRepository audit,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var contact = await contacts.GetAsync(tenantId, contactId, cancellationToken) ?? throw new NotFoundException();
        var leads = ContactView.LeadFields(contact, encryptor);
        var tenantRow = await tenants.GetAsync(tenantId, cancellationToken);
        var labels = IndustryTemplateRegistry.Get(tenantRow?.IndustrySlug).LeadFields.ToDictionary(f => f.Key, f => f.Label, StringComparer.Ordinal);
        var names = await CustomerEndpoints.MemberNamesAsync(tenantId, memberships, cancellationToken);
        var messages = await contacts.ListAllMessagesAsync(tenantId, contactId, cancellationToken);
        var notes = await contacts.ListNotesAsync(tenantId, contactId, int.MaxValue, cancellationToken);
        var suggestions = await contacts.ListSuggestionsForContactAsync(tenantId, contactId, cancellationToken);

        var profile = new List<IReadOnlyList<string>>
        {
            Row("Tên", ContactView.Name(contact, leads)),
            Row("Kênh", contact.Channel.ToString()),
            Row("Mã người dùng trên kênh", contact.ExternalUserId),
            Row("Mức độ tiềm năng", ContactView.StatusLabel(contact.LeadStatus)),
            Row("Nhãn", string.Join(", ", contact.Tags)),
            Row("Cách gọi khách", CustomerSalutation.Resolve(contact.Salutation)),
        };
        profile.AddRange(leads.Select(kv => Row(labels.GetValueOrDefault(kv.Key) ?? kv.Key, kv.Value)));
        profile.Add(Row("Nhận tin chủ động", contact.ProactiveOptOutAt is { } optOut ? $"Đã tắt lúc {ContactDataExcel.Format(optOut)}" : "Có"));
        profile.Add(Row("Ngày bắt đầu", ContactDataExcel.Format(contact.CreatedAt)));
        profile.Add(Row("Tin cuối của khách", ContactDataExcel.Format(contact.LastCustomerMessageAt)));

        var sheets = new List<ExcelSheet>
        {
            new("Thông tin", ["Mục", "Giá trị"], profile),
            new(
                "Tin nhắn",
                ["Thời gian", "Người gửi", "Nội dung"],
                messages.Select(m => Row(ContactDataExcel.Format(m.CreatedAt), SenderLabel(m.Sender), encryptor.Decrypt(m.ContentEnc))).ToList()),
            new(
                "Nhật ký chăm sóc",
                ["Ngày", "Loại", "Nội dung", "Chăm sóc lại lúc", "Người ghi"],
                notes.Reverse().Select(n => Row(
                    n.HappenedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    NoteKindLabel(n.Kind),
                    encryptor.Decrypt(n.ContentEnc),
                    ContactDataExcel.Format(n.FollowUpAt),
                    n.AuthorUserId is { } a ? names.GetValueOrDefault(a) ?? "" : "")).ToList()),
            new(
                "Gợi ý chăm sóc",
                ["Lúc tạo", "Trạng thái", "Lý do", "Tin nháp", "Kết quả"],
                suggestions.Select(s => Row(
                    ContactDataExcel.Format(s.CreatedAt),
                    s.Status.ToString(),
                    encryptor.Decrypt(s.ReasonEnc),
                    s.DraftEnc is null ? "" : encryptor.Decrypt(s.DraftEnc),
                    s.Outcome ?? "")).ToList()),
        };

        audit.Add(tenantId, tenant.UserId, "contact.data_exported", contactId.ToString());
        await audit.SaveChangesAsync(cancellationToken);

        using var buffer = new MemoryStream();
        ContactDataExcel.Write(sheets, buffer);
        var stamp = DateTime.UtcNow.AddHours(7).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return TypedResults.File(buffer.ToArray(), XlsxContentType, $"du-lieu-khach-{stamp}.xlsx");
    }

    /// <summary>Xóa vĩnh viễn khách + hội thoại + tin nhắn + ghi chú + gợi ý (không hoàn tác). Audit log chỉ giữ mã khách.</summary>
    private static async Task<NoContent> DeleteAsync(
        Guid contactId,
        ITenantContext tenant,
        ContactRepository contacts,
        AuditLogRepository audit,
        IInboxNotifier notifier,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversations = await contacts.ListConversationsAsync(tenantId, contactId, cancellationToken);
        if (await contacts.DeleteAsync(tenantId, contactId, cancellationToken) == 0)
        {
            throw new NotFoundException();
        }

        audit.Add(tenantId, tenant.UserId, "contact.deleted", $"{contactId}:conversations={conversations.Count}");
        await audit.SaveChangesAsync(cancellationToken);
        foreach (var conversation in conversations)
        {
            await notifier.PublishAsync(tenantId, new InboxEvent("updated", conversation.Id), cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static string[] Row(params string[] values) => values;

    private static string SenderLabel(MessageSender sender) => sender switch
    {
        MessageSender.Customer => "Khách",
        MessageSender.Bot => "Trợ lý AI",
        MessageSender.Staff => "Nhân viên",
        _ => "Hệ thống",
    };

    private static string NoteKindLabel(ContactNoteKind kind) => kind switch
    {
        ContactNoteKind.Service => "Đã dùng dịch vụ",
        ContactNoteKind.Appointment => "Lịch hẹn",
        _ => "Ghi chú",
    };
}
