using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZaloAi.Api.Auth;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Customers;

/// <param name="Action">new | update | error</param>
/// <param name="Salutation">anh | chị | cô | chú | bác — null nếu không có giới tính/cách gọi.</param>
public sealed record ContactImportPreviewItem(
    int Row,
    string? Phone,
    string? Name,
    string? Salutation,
    string? Service,
    DateTimeOffset? FollowUpAt,
    bool? Consent,
    string Action,
    string? ExistingName,
    IReadOnlyList<string> Errors);

public sealed record ContactImportPreviewResponse(IReadOnlyList<ContactImportPreviewItem> Rows, int New, int Update, int Error);

/// <param name="FollowUps">Số lịch hẹn chăm sóc mới/đổi giờ.</param>
public sealed record ContactImportResultResponse(int Created, int Updated, int Skipped, int FollowUps);

/// <param name="Salutation">Cách gọi khách (anh/chị/cô/chú/bác, hoặc "mình").</param>
/// <param name="Kind">note | service | appointment</param>
public sealed record PhoneDueItem(
    Guid NoteId,
    Guid ContactId,
    string Name,
    string? Phone,
    string Salutation,
    string Note,
    string Kind,
    DateOnly HappenedOn,
    DateTimeOffset FollowUpAt,
    bool ConsentMarketing);

/// <summary>
/// Nhập danh sách khách (Excel) — chủ và nhân viên; và danh sách "tới hẹn – chưa nhắn OA được" (khách chỉ có SĐT) để nhân viên gọi
/// điện. Upload multipart: không dùng antiforgery token, CSRF bị chặn bởi cookie SameSite=Lax (DECISIONS.md M1 bước 4).
/// </summary>
internal static class ContactImportEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapContactImportEndpoints(this IEndpointRouteBuilder app)
    {
        var import = app.MapGroup("/contacts/import").WithTags("Customers").RequireTenantRole(TenantRole.Staff);
        import.MapGet("/template", DownloadTemplate).Produces(StatusCodes.Status200OK, contentType: XlsxContentType);
        import.MapPost("/preview", PreviewAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ContactImportExcel.MaxFileBytes + (64 * 1024)))
            .ProducesProblem(StatusCodes.Status400BadRequest);
        import.MapPost("", ApplyAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ContactImportExcel.MaxFileBytes + (64 * 1024)))
            .ProducesProblem(StatusCodes.Status400BadRequest);

        var due = app.MapGroup("/care/phone-due").WithTags("Care").RequireTenantRole(TenantRole.Staff);
        due.MapGet("", ListDueAsync);
        due.MapPost("/{noteId:guid}/done", MarkDoneAsync).ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static FileContentHttpResult DownloadTemplate()
    {
        using var buffer = new MemoryStream();
        ContactImportExcel.WriteTemplate(buffer);
        return TypedResults.File(buffer.ToArray(), XlsxContentType, "mau-danh-sach-khach.xlsx");
    }

    private static async Task<Ok<ContactImportPreviewResponse>> PreviewAsync(
        IFormFile file, ITenantContext tenant, ContactImportService imports, TimeProvider time, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(file, time, cancellationToken);
        var preview = await imports.PreviewAsync(tenant.RequireTenantId(), rows, cancellationToken);
        var items = preview.Select(p => new ContactImportPreviewItem(
            p.Row.Row, p.Row.Phone, p.Row.Name, p.Row.Salutation, p.Row.Service, p.Row.FollowUpAt, p.Row.Consent, p.Action, p.ExistingName, p.Row.Errors)).ToList();
        return TypedResults.Ok(new ContactImportPreviewResponse(
            items, items.Count(i => i.Action == "new"), items.Count(i => i.Action == "update"), items.Count(i => i.Action == "error")));
    }

    /// <summary>Nhập các dòng hợp lệ (dòng lỗi bỏ qua). Gửi lại đúng file đã xem trước.</summary>
    private static async Task<Ok<ContactImportResultResponse>> ApplyAsync(
        IFormFile file, ITenantContext tenant, ContactImportService imports, TimeProvider time, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(file, time, cancellationToken);
        var result = await imports.ApplyAsync(tenant.RequireTenantId(), rows, cancellationToken);
        return TypedResults.Ok(new ContactImportResultResponse(result.Created, result.Updated, result.Skipped, result.FollowUps));
    }

    private static async Task<IReadOnlyList<ContactImportRow>> ReadAsync(IFormFile file, TimeProvider time, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new InvalidInputException("File rỗng.");
        }

        if (file.Length > ContactImportExcel.MaxFileBytes)
        {
            throw new InvalidInputException("File quá 5MB. Chia thành nhiều file nhỏ hơn.");
        }

        // ClosedXML cần stream đọc được nhiều lần → chép ra bộ nhớ (tối đa 5MB).
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        try
        {
            return ContactImportExcel.Read(buffer, time.GetUtcNow());
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidInputException(ex.Message);
        }
    }

    private static async Task<Ok<List<PhoneDueItem>>> ListDueAsync(
        ITenantContext tenant, ContactRepository contacts, IFieldEncryptor encryptor, TimeProvider time, CancellationToken cancellationToken)
    {
        var rows = await contacts.ListPhoneOnlyDueAsync(tenant.RequireTenantId(), time.GetUtcNow(), 200, cancellationToken);
        return TypedResults.Ok(rows.Select(r =>
        {
            var leads = ContactView.LeadFields(r.Contact, encryptor);
            return new PhoneDueItem(
                r.Note.Id,
                r.Contact.Id,
                ContactView.Name(r.Contact, leads),
                r.Contact.PhoneEnc is null ? leads.GetValueOrDefault("phone") : encryptor.Decrypt(r.Contact.PhoneEnc),
                CustomerSalutation.Resolve(r.Contact.Salutation),
                encryptor.Decrypt(r.Note.ContentEnc),
                r.Note.Kind.ToString().ToLowerInvariant(),
                r.Note.HappenedOn,
                r.Note.FollowUpAt!.Value,
                r.Contact.ConsentMarketing);
        }).ToList());
    }

    /// <summary>Nhân viên đã gọi/nhắn khách theo lịch hẹn → ẩn khỏi danh sách (có audit log).</summary>
    private static async Task<NoContent> MarkDoneAsync(
        Guid noteId, ITenantContext tenant, ContactRepository contacts, AuditLogRepository audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var note = await contacts.GetNoteAsync(tenantId, noteId, cancellationToken) ?? throw new NotFoundException();
        note.FollowUpDoneAt ??= time.GetUtcNow();
        audit.Add(tenantId, tenant.UserId, "care.phone_follow_up_done", noteId.ToString());
        await contacts.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
