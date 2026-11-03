using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Customers;

/// <param name="Action">new (khách mới) | update (đã có khách cùng SĐT) | error (dòng lỗi, bỏ qua)</param>
/// <param name="ExistingName">Tên khách đang có khi Action = update.</param>
public sealed record ContactImportPreviewRow(ContactImportRow Row, string Action, string? ExistingName);

public sealed record ContactImportResult(int Created, int Updated, int Skipped, int FollowUps);

/// <summary>
/// Nhập danh sách khách của doanh nghiệp (chủ và nhân viên). Khớp khách theo dấu vân tay SĐT trong tenant; khách mới chưa nhắn qua
/// kênh nào thì tạo với kênh "Chỉ có SĐT". Mỗi dòng: tạo/cập nhật hồ sơ (tên, SĐT mã hóa, cách gọi, đồng ý nhận tin) + ghi chú
/// "đã dùng dịch vụ" / "ghi chú cho AI" kèm giờ hẹn chăm sóc lại (dùng lại cơ chế hẹn của nhật ký chăm sóc). Nhập lại cùng file
/// không tạo trùng ghi chú.
/// </summary>
public sealed class ContactImportService(AppDbContext db, ITenantContext tenantContext, IFieldEncryptor encryptor, IPhoneHasher hasher, TimeProvider time)
    : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<ContactImportPreviewRow>> PreviewAsync(Guid tenantId, IReadOnlyList<ContactImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EnsureTenant(tenantId);
        var existing = await ByPhoneHashAsync(tenantId, cancellationToken);
        return rows.Select(r =>
        {
            if (!r.Valid)
            {
                return new ContactImportPreviewRow(r, "error", null);
            }

            return existing.TryGetValue(hasher.Hash(r.Phone!), out var c)
                ? new ContactImportPreviewRow(r, "update", ContactView.Name(c, ContactView.LeadFields(c, encryptor)))
                : new ContactImportPreviewRow(r, "new", null);
        }).ToList();
    }

    public async Task<ContactImportResult> ApplyAsync(Guid tenantId, IReadOnlyList<ContactImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EnsureTenant(tenantId);
        var existing = await ByPhoneHashAsync(tenantId, cancellationToken);
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime);
        int created = 0, updated = 0, followUps = 0;

        foreach (var row in rows.Where(r => r.Valid))
        {
            var hash = hasher.Hash(row.Phone!);
            if (!existing.TryGetValue(hash, out var contact))
            {
                contact = new Contact
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Channel = ChannelKind.Phone,
                    ExternalUserId = hash,
                    DisplayName = row.Name,
                    PhoneEnc = encryptor.Encrypt(row.Phone!),
                    PhoneHash = hash,
                };
                Db.Contacts.Add(contact);
                existing[hash] = contact;
                created++;
            }
            else
            {
                updated++;
                if (contact.Channel == ChannelKind.Phone && row.Name is not null)
                {
                    contact.DisplayName = row.Name;
                }
            }

            MergeLeadFields(contact, row);
            if (row.Salutation is not null)
            {
                contact.Salutation = row.Salutation;
                contact.SalutationSource = CustomerSalutation.SourceStaff;
            }

            if (row.Consent is { } consent && (contact.ConsentMarketing != consent || contact.ConsentSource is null))
            {
                contact.ConsentMarketing = consent;
                contact.ConsentAt = now;
                contact.ConsentSource = "import";
            }

            followUps += await AddNotesAsync(tenantId, contact, row, today, cancellationToken);
        }

        Db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            UserId = _caller.UserId,
            Action = "contacts.imported",
            Target = $"created={created};updated={updated};skipped={rows.Count(r => !r.Valid)}",
        });
        await Db.SaveChangesAsync(cancellationToken);
        return new ContactImportResult(created, updated, rows.Count(r => !r.Valid), followUps);
    }

    /// <summary>Ghi chú dịch vụ + ghi chú cho AI; giờ hẹn gắn vào ghi chú dịch vụ (hoặc ghi chú AI, hoặc ghi chú hẹn riêng). Trả 1 nếu có hẹn mới.</summary>
    private async Task<int> AddNotesAsync(Guid tenantId, Contact contact, ContactImportRow row, DateOnly today, CancellationToken cancellationToken)
    {
        var known = Db.Entry(contact).State == EntityState.Added
            ? []
            : await Db.ContactNotes.Where(n => n.TenantId == tenantId && n.ContactId == contact.Id).ToListAsync(cancellationToken);

        ContactNote? Upsert(ContactNoteKind kind, string text, DateOnly on)
        {
            var same = known.FirstOrDefault(n => n.Kind == kind && n.HappenedOn == on && encryptor.Decrypt(n.ContentEnc) == text)
                ?? Db.ContactNotes.Local.FirstOrDefault(n => n.ContactId == contact.Id && n.Kind == kind && n.HappenedOn == on && encryptor.Decrypt(n.ContentEnc) == text);
            if (same is not null)
            {
                return same;
            }

            var note = new ContactNote
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ContactId = contact.Id,
                AuthorUserId = _caller.UserId,
                Kind = kind,
                ContentEnc = encryptor.Encrypt(text),
                HappenedOn = on,
            };
            Db.ContactNotes.Add(note);
            return note;
        }

        var service = row.Service is null ? null : Upsert(ContactNoteKind.Service, row.Service, row.ServiceDate ?? today);
        var aiNote = row.AiNote is null ? null : Upsert(ContactNoteKind.Note, $"Ghi chú cho AI: {row.AiNote}", today);
        if (row.FollowUpAt is not { } at)
        {
            return 0;
        }

        var target = service ?? aiNote ?? Upsert(ContactNoteKind.Note, "Hẹn chăm sóc lại (nhập từ danh sách khách)", today)!;
        if (target.FollowUpAt == at)
        {
            return 0;
        }

        target.FollowUpAt = at;
        target.FollowUpQueuedAt = null;
        target.FollowUpDoneAt = null;
        return 1;
    }

    private void MergeLeadFields(Contact contact, ContactImportRow row)
    {
        var fields = ContactView.LeadFields(contact, encryptor);
        var changed = false;
        if (row.Name is not null && !fields.ContainsKey("name"))
        {
            fields["name"] = row.Name;
            changed = true;
        }

        if (!fields.ContainsKey("phone"))
        {
            fields["phone"] = row.Phone!;
            changed = true;
        }

        if (changed)
        {
            contact.LeadFieldsEnc = encryptor.Encrypt(JsonSerializer.Serialize(fields));
        }

        if (contact.PhoneHash is null)
        {
            contact.PhoneEnc = encryptor.Encrypt(row.Phone!);
            contact.PhoneHash = hasher.Hash(row.Phone!);
        }
    }

    /// <summary>
    /// Khách của tenant theo dấu vân tay SĐT. Khách cũ đã tự cho SĐT khi chat (nằm trong thông tin khách, chưa có dấu vân tay) được
    /// điền dấu vân tay luôn để nhập danh sách khớp đúng người.
    /// </summary>
    private async Task<Dictionary<string, Contact>> ByPhoneHashAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var contacts = await Db.Contacts.Where(c => c.TenantId == tenantId && c.Channel != ChannelKind.Webchat)
            .Take(ContactRepository.MaxContacts)
            .ToListAsync(cancellationToken);
        var result = contacts.Where(c => c.PhoneHash is not null).ToDictionary(c => c.PhoneHash!, StringComparer.Ordinal);
        foreach (var contact in contacts.Where(c => c.PhoneHash is null && c.LeadFieldsEnc is not null))
        {
            if (PhoneNumber.Normalize(ContactView.LeadFields(contact, encryptor).GetValueOrDefault("phone")) is { } phone
                && result.TryAdd(hasher.Hash(phone), contact))
            {
                contact.PhoneEnc = encryptor.Encrypt(phone);
                contact.PhoneHash = hasher.Hash(phone);
            }
        }

        return result;
    }
}

/// <summary>
/// Khách nhắn qua kênh (Zalo, website) và tự cho SĐT: gắn SĐT vào hồ sơ; nếu SĐT đó đang thuộc một khách "chỉ có SĐT" do DN nhập
/// thì GỘP vào hồ sơ đang chat (nhật ký chăm sóc, cách gọi, đồng ý nhận tin, nhãn, thông tin khách) rồi xóa bản nhập. Chỉ trong
/// cùng tenant. SĐT trùng với một khách đang chat khác thì không gắn (tránh ghép nhầm hai người).
/// </summary>
public sealed class ContactPhoneLinker(AppDbContext db, ITenantContext tenantContext, IFieldEncryptor encryptor, IPhoneHasher hasher)
    : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Gọi trước SaveChanges của job (thay đổi trên <paramref name="contact"/> được lưu cùng lượt).</summary>
    public async Task LinkAsync(Guid tenantId, Contact contact, string? rawPhone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        EnsureTenant(tenantId);
        if (contact.PhoneHash is not null || contact.Channel == ChannelKind.Phone || PhoneNumber.Normalize(rawPhone) is not { } phone)
        {
            return;
        }

        var hash = hasher.Hash(phone);
        var other = await Db.Contacts.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.PhoneHash == hash && c.Id != contact.Id, cancellationToken);
        if (other is not null && other.Channel != ChannelKind.Phone)
        {
            return;
        }

        if (other is not null)
        {
            MergeImported(other, contact);
            var otherId = other.Id;
            await Db.ContactNotes.Where(n => n.TenantId == tenantId && n.ContactId == otherId)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ContactId, contact.Id), cancellationToken);
            Db.Entry(other).State = EntityState.Detached;
            await Db.Contacts.Where(c => c.TenantId == tenantId && c.Id == otherId).ExecuteDeleteAsync(cancellationToken);
        }

        contact.PhoneEnc = encryptor.Encrypt(phone);
        contact.PhoneHash = hash;
    }

    private void MergeImported(Contact imported, Contact contact)
    {
        if (imported.SalutationSource == CustomerSalutation.SourceStaff && contact.SalutationSource != CustomerSalutation.SourceStaff)
        {
            contact.Salutation = imported.Salutation;
            contact.SalutationSource = imported.SalutationSource;
        }

        if (imported.ConsentSource is not null && contact.ConsentSource is null)
        {
            contact.ConsentMarketing = imported.ConsentMarketing;
            contact.ConsentAt = imported.ConsentAt;
            contact.ConsentSource = imported.ConsentSource;
        }

        contact.Tags = contact.Tags.Union(imported.Tags, StringComparer.OrdinalIgnoreCase).ToList();
        if (imported.LeadStatusManual && !contact.LeadStatusManual)
        {
            contact.LeadStatus = imported.LeadStatus;
            contact.LeadStatusManual = true;
        }

        var fields = ContactView.LeadFields(contact, encryptor);
        foreach (var (key, value) in ContactView.LeadFields(imported, encryptor))
        {
            fields.TryAdd(key, value);
        }

        contact.LeadFieldsEnc = encryptor.Encrypt(JsonSerializer.Serialize(fields));
    }
}
