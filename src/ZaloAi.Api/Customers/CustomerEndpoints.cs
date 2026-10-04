using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.IndustryTemplates;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Customers;

/// <param name="LeadStatus">new | interested | hot | won | lost</param>
/// <param name="LeadStatusManual">Nhân viên đã tự đặt (hệ thống không tự đổi).</param>
/// <param name="Phone">Số điện thoại khách tự cung cấp cho bot (nếu có).</param>
/// <param name="Interest">Nhu cầu / dịch vụ quan tâm (nếu có).</param>
/// <param name="ProactiveOptOut">null = bot được chủ động nhắn; customer = khách đã nhắn "hủy" (không bật lại được); staff = nhân viên tắt.</param>
public sealed record ContactListItem(
    Guid Id,
    string Name,
    string Channel,
    bool IsTest,
    string LeadStatus,
    bool LeadStatusManual,
    IReadOnlyList<string> Tags,
    string? Phone,
    string? Interest,
    DateTimeOffset? LastCustomerMessageAt,
    DateTimeOffset CreatedAt,
    string? ProactiveOptOut,
    DateTimeOffset? LastProactiveAt);

/// <param name="Counts">Số khách theo trạng thái (new, interested, hot, won, lost) — không phụ thuộc bộ lọc.</param>
/// <param name="Truncated">Có nhiều khách hơn giới hạn tải một lần.</param>
public sealed record ContactListResponse(IReadOnlyList<ContactListItem> Items, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> AllTags, bool Truncated);

/// <param name="Kind">note | service | appointment</param>
public sealed record ContactNoteItem(
    Guid Id,
    string Kind,
    string Text,
    DateOnly HappenedOn,
    DateTimeOffset? FollowUpAt,
    Guid? AuthorUserId,
    string? AuthorName,
    DateTimeOffset CreatedAt);

/// <param name="Mode">bot | human</param>
public sealed record ContactConversationItem(Guid Id, string Channel, bool IsTest, string Mode, DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt);

/// <param name="LeadFields">Thông tin khách tự cung cấp, kèm nhãn theo mẫu ngành.</param>
public sealed record ContactDetail(
    ContactListItem Contact,
    IReadOnlyList<LeadFieldValue> LeadFields,
    IReadOnlyList<ContactNoteItem> Notes,
    IReadOnlyList<ContactConversationItem> Conversations,
    IReadOnlyList<CareSuggestionItem> Suggestions);

public sealed record LeadFieldValue(string Key, string Label, string Value);

/// <param name="LeadStatus">new | interested | hot | won | lost | auto (trả lại cho hệ thống tự xếp); null = giữ nguyên.</param>
/// <param name="Tags">null = giữ nguyên.</param>
/// <param name="ProactiveEnabled">Cho bot chủ động nhắn khách này; null = giữ nguyên. Không bật lại được khi khách tự từ chối.</param>
public sealed record UpdateContactRequest(string? LeadStatus, IReadOnlyList<string>? Tags, bool? ProactiveEnabled = null);

/// <param name="Kind">note | service | appointment</param>
/// <param name="FollowUpAt">Lúc cần chăm sóc lại (kèm múi giờ, vd 2026-10-12T09:30:00+07:00) — tới giờ sẽ hiện ở trang "Cần chăm sóc".</param>
public sealed record AddContactNoteRequest(string Kind, string Text, DateOnly HappenedOn, DateTimeOffset? FollowUpAt);

internal sealed class UpdateContactRequestValidator : AbstractValidator<UpdateContactRequest>
{
    public UpdateContactRequestValidator()
    {
        RuleFor(x => x.LeadStatus)
            .Must(s => s is null || s == "auto" || CustomerEndpoints.TryParseStatus(s, out _))
            .WithMessage("Trạng thái không hợp lệ.");
        RuleFor(x => x.Tags!.Count).LessThanOrEqualTo(20).WithName("Số nhãn").When(x => x.Tags is not null);
        RuleForEach(x => x.Tags).NotEmpty().MaximumLength(30).WithName("Nhãn");
    }
}

internal sealed class AddContactNoteRequestValidator : AbstractValidator<AddContactNoteRequest>
{
    public AddContactNoteRequestValidator()
    {
        RuleFor(x => x.Kind).Must(k => k is "note" or "service" or "appointment").WithMessage("Loại ghi chú không hợp lệ.");
        RuleFor(x => x.Text).NotEmpty().MaximumLength(2000).WithName("Nội dung");
        RuleFor(x => x.HappenedOn).InclusiveBetween(new DateOnly(2000, 1, 1), new DateOnly(2100, 1, 1)).WithName("Ngày");
        RuleFor(x => x.FollowUpAt)
            .Must((x, at) => DateOnly.FromDateTime(at!.Value.ToOffset(TimeSpan.FromHours(7)).DateTime) >= x.HappenedOn)
            .WithMessage("Lúc chăm sóc lại phải từ ngày ghi chú trở đi.")
            .When(x => x.FollowUpAt is not null);
    }
}

/// <summary>
/// Khách hàng / khách tiềm năng (CLAUDE.md M6). tenantId luôn từ ITenantContext. Tên/SĐT nằm trong trường mã hóa nên tìm kiếm làm
/// trong bộ nhớ sau khi giải mã (tối đa <see cref="ContactRepository.MaxContacts"/> khách). Xem hồ sơ và xuất Excel ghi audit log.
/// </summary>
internal static class CustomerEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/contacts").WithTags("Customers");
        group.MapGet("", ListAsync).RequireTenantRole(TenantRole.Staff);
        group.MapGet("/export", ExportAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Produces(StatusCodes.Status200OK, contentType: XlsxContentType);
        group.MapGet("/{contactId:guid}", GetAsync).RequireTenantRole(TenantRole.Staff).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPatch("/{contactId:guid}", UpdateAsync)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<UpdateContactRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{contactId:guid}/notes", AddNoteAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<AddContactNoteRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapDelete("/{contactId:guid}/notes/{noteId:guid}", DeleteNoteAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    /// <param name="status">new | interested | hot | won | lost</param>
    /// <param name="q">Tìm theo tên hoặc số điện thoại.</param>
    private static async Task<Ok<ContactListResponse>> ListAsync(
        string? status,
        string? tag,
        string? q,
        bool? includeTest,
        ITenantContext tenant,
        ContactRepository contacts,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var test = includeTest ?? true;
        var parsed = TryParseStatus(status, out var s) ? s : (LeadStatus?)null;
        var rows = await contacts.ListAsync(tenantId, parsed, tag, test, cancellationToken);
        var items = Filter(rows.Select(c => ToItem(c, ContactView.LeadFields(c, encryptor))), q).Take(500).ToList();

        var counts = await contacts.CountByStatusAsync(tenantId, test, cancellationToken);
        var tags = await contacts.ListTagsAsync(tenantId, cancellationToken);
        return TypedResults.Ok(new ContactListResponse(
            items,
            Enum.GetValues<LeadStatus>().ToDictionary(Lower, v => counts.GetValueOrDefault(v)),
            tags,
            rows.Count >= ContactRepository.MaxContacts));
    }

    private static async Task<Ok<ContactDetail>> GetAsync(
        Guid contactId,
        ITenantContext tenant,
        ContactRepository contacts,
        TenantRepository tenants,
        MembershipRepository memberships,
        AuditLogRepository audit,
        IDistributedStore store,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var contact = await contacts.GetAsync(tenantId, contactId, cancellationToken) ?? throw new NotFoundException();
        var leads = ContactView.LeadFields(contact, encryptor);
        var tenantRow = await tenants.GetAsync(tenantId, cancellationToken);
        var labels = IndustryTemplateRegistry.Get(tenantRow?.IndustrySlug).LeadFields.ToDictionary(f => f.Key, f => f.Label, StringComparer.Ordinal);
        var names = await MemberNamesAsync(tenantId, memberships, cancellationToken);
        var notes = await contacts.ListNotesAsync(tenantId, contactId, 200, cancellationToken);
        var conversations = await contacts.ListConversationsAsync(tenantId, contactId, cancellationToken);
        var suggestions = await contacts.ListSuggestionsForContactAsync(tenantId, contactId, cancellationToken);

        if (await store.SetIfNotExistsAsync($"audit:contact-view:{tenant.UserId:N}:{contactId:N}", "1", TimeSpan.FromMinutes(30), cancellationToken))
        {
            audit.Add(tenantId, tenant.UserId, "contact.viewed", contactId.ToString());
            await audit.SaveChangesAsync(cancellationToken);
        }

        var item = ToItem(contact, leads);
        return TypedResults.Ok(new ContactDetail(
            item,
            leads.Select(kv => new LeadFieldValue(kv.Key, labels.GetValueOrDefault(kv.Key) ?? kv.Key, kv.Value)).ToList(),
            notes.Select(n => ToNote(n, names, encryptor)).ToList(),
            conversations.Select(c => new ContactConversationItem(c.Id, Lower(c.Channel), c.IsTest, Lower(c.Mode), c.LastMessageAt, c.CreatedAt)).ToList(),
            suggestions.Select(x => CareEndpoints.ToItem(x, contact, item.Name, names, encryptor)).ToList()));
    }

    private static async Task<Ok<ContactListItem>> UpdateAsync(
        Guid contactId,
        UpdateContactRequest request,
        ITenantContext tenant,
        ContactRepository contacts,
        AuditLogRepository audit,
        IFieldEncryptor encryptor,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var contact = await contacts.GetAsync(tenantId, contactId, cancellationToken) ?? throw new NotFoundException();
        if (request.LeadStatus == "auto")
        {
            contact.LeadStatusManual = false;
        }
        else if (TryParseStatus(request.LeadStatus, out var status))
        {
            if (status != contact.LeadStatus)
            {
                contact.LeadStatus = status;
                contact.LeadStatusChangedAt = time.GetUtcNow();
            }

            contact.LeadStatusManual = true;
            audit.Add(tenantId, tenant.UserId, "contact.status_changed", $"{contactId}:{Lower(status)}");
        }

        if (request.ProactiveEnabled is { } enabled)
        {
            if (enabled && contact.ProactiveOptOutAt is not null)
            {
                if (contact.ProactiveOptOutSource != "staff")
                {
                    throw new ConflictException("Khách đã tự nhắn từ chối nhận tin, không thể bật lại.", "customer_opted_out");
                }

                contact.ProactiveOptOutAt = null;
                contact.ProactiveOptOutSource = null;
            }
            else if (!enabled && contact.ProactiveOptOutAt is null)
            {
                contact.ProactiveOptOutAt = time.GetUtcNow();
                contact.ProactiveOptOutSource = "staff";
            }

            audit.Add(tenantId, tenant.UserId, "contact.proactive_changed", $"{contactId}:{enabled}");
        }

        if (request.Tags is not null)
        {
            contact.Tags = request.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        await contacts.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToItem(contact, ContactView.LeadFields(contact, encryptor)));
    }

    private static async Task<Ok<ContactNoteItem>> AddNoteAsync(
        Guid contactId,
        AddContactNoteRequest request,
        ITenantContext tenant,
        ContactRepository contacts,
        MembershipRepository memberships,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        _ = await contacts.GetAsync(tenantId, contactId, cancellationToken) ?? throw new NotFoundException();
        var note = contacts.AddNote(tenantId, new ContactNote
        {
            ContactId = contactId,
            AuthorUserId = tenant.UserId,
            Kind = Enum.Parse<ContactNoteKind>(request.Kind, ignoreCase: true),
            ContentEnc = encryptor.Encrypt(request.Text.Trim()),
            HappenedOn = request.HappenedOn,
            FollowUpAt = request.FollowUpAt?.ToUniversalTime(), // Postgres timestamptz chỉ nhận UTC
        });
        await contacts.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToNote(note, await MemberNamesAsync(tenantId, memberships, cancellationToken), encryptor));
    }

    /// <summary>Người viết xóa được ghi chú của mình; owner xóa được mọi ghi chú.</summary>
    private static async Task<NoContent> DeleteNoteAsync(
        Guid contactId,
        Guid noteId,
        ITenantContext tenant,
        ContactRepository contacts,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var note = await contacts.GetNoteAsync(tenantId, noteId, cancellationToken);
        if (note is null || note.ContactId != contactId)
        {
            throw new NotFoundException();
        }

        if (note.AuthorUserId != tenant.UserId && tenant.Role != TenantRole.Owner)
        {
            throw new ForbiddenException("Chỉ người viết hoặc chủ doanh nghiệp được xóa ghi chú này.");
        }

        contacts.RemoveNote(tenantId, note);
        audit.Add(tenantId, tenant.UserId, "contact.note_deleted", $"{contactId}:{noteId}");
        await contacts.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Xuất Excel danh sách khách (cùng bộ lọc với trang). Chỉ owner; ghi audit log (dữ liệu cá nhân rời khỏi hệ thống).</summary>
    private static async Task<FileContentHttpResult> ExportAsync(
        string? status,
        string? tag,
        string? q,
        bool? includeTest,
        ITenantContext tenant,
        ContactRepository contacts,
        TenantRepository tenants,
        AuditLogRepository audit,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var parsed = TryParseStatus(status, out var s) ? s : (LeadStatus?)null;
        var rows = await contacts.ListAsync(tenantId, parsed, tag, includeTest ?? false, cancellationToken);
        var withFields = rows.Select(c => (Contact: c, Leads: ContactView.LeadFields(c, encryptor))).ToList();
        var ids = Filter(withFields.Select(x => ToItem(x.Contact, x.Leads)), q).Select(i => i.Id).ToHashSet();
        var tenantRow = await tenants.GetAsync(tenantId, cancellationToken);
        var fields = IndustryTemplateRegistry.Get(tenantRow?.IndustrySlug).LeadFields
            .Select(f => new KeyValuePair<string, string>(f.Key, f.Label)).ToList();

        var export = withFields.Where(x => ids.Contains(x.Contact.Id))
            .Select(x => new ContactExportRow(
                ContactView.Name(x.Contact, x.Leads), Lower(x.Contact.Channel), x.Contact.LeadStatus, x.Contact.Tags, x.Leads,
                x.Contact.LastCustomerMessageAt, x.Contact.CreatedAt))
            .ToList();

        audit.Add(tenantId, tenant.UserId, "contacts.exported", $"xlsx:{export.Count}");
        await audit.SaveChangesAsync(cancellationToken);

        using var buffer = new MemoryStream();
        ContactExcel.Write(export, fields, buffer);
        var stamp = DateTime.UtcNow.AddHours(7).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return TypedResults.File(buffer.ToArray(), XlsxContentType, $"khach-hang-{stamp}.xlsx");
    }

    internal static bool TryParseStatus(string? value, out LeadStatus status)
    {
        status = default;
        return value is not null && !int.TryParse(value, out _) && Enum.TryParse(value, ignoreCase: true, out status) && Enum.IsDefined(status);
    }

    private static IEnumerable<ContactListItem> Filter(IEnumerable<ContactListItem> items, string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return items;
        }

        var text = q.Trim();
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return items.Where(i =>
            i.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (digits.Length >= 3 && i.Phone is not null && new string(i.Phone.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)));
    }

    internal static ContactListItem ToItem(Contact c, IReadOnlyDictionary<string, string> leads) => new(
        c.Id,
        ContactView.Name(c, leads),
        Lower(c.Channel),
        c.Channel == ChannelKind.Webchat,
        Lower(c.LeadStatus),
        c.LeadStatusManual,
        c.Tags,
        leads.GetValueOrDefault("phone"),
        leads.GetValueOrDefault("service_interest") ?? leads.GetValueOrDefault("need"),
        c.LastCustomerMessageAt,
        c.CreatedAt,
        c.ProactiveOptOutAt is null ? null : c.ProactiveOptOutSource ?? "customer",
        c.LastProactiveAt);

    private static ContactNoteItem ToNote(ContactNote n, IReadOnlyDictionary<Guid, string> names, IFieldEncryptor encryptor) => new(
        n.Id,
        Lower(n.Kind),
        encryptor.Decrypt(n.ContentEnc),
        n.HappenedOn,
        n.FollowUpAt,
        n.AuthorUserId,
        n.AuthorUserId is { } a ? names.GetValueOrDefault(a) : null,
        n.CreatedAt);

    internal static async Task<Dictionary<Guid, string>> MemberNamesAsync(Guid tenantId, MembershipRepository memberships, CancellationToken cancellationToken) =>
        (await memberships.ListAsync(tenantId, cancellationToken)).ToDictionary(m => m.UserId, m => m.User?.Name ?? "");

    internal static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
