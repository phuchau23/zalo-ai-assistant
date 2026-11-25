using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Một người trong danh sách "Cần gọi điện" (SĐT đầy đủ để bấm gọi — xem danh sách có ghi nhật ký truy cập).</summary>
/// <param name="Since">Lúc vào danh sách / lần cuối có người xử lý (đếm 30 ngày trước khi lưu trữ).</param>
/// <param name="LastCustomerText">Tin cuối của khách (chỉ với người đã nguội — từng tư vấn), cắt ngắn.</param>
public sealed record CallListEntry(
    Guid TargetId,
    Guid ContactId,
    string Name,
    string? Phone,
    string Salutation,
    OutreachState State,
    string Origin,
    string? ReferrerName,
    Guid? ConnectionId,
    string? NickName,
    Guid? ConversationId,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? FriendRequestSentAt,
    DateTimeOffset? LastCustomerMessageAt,
    string? LastCustomerText,
    DateTimeOffset Since,
    int CallAttempts,
    DateTimeOffset? LastCallAt);

/// <summary>
/// Danh sách "Cần gọi điện" ở trang Cần chăm sóc (chủ dự án chốt 2026-11-25): người bot đã thôi nhắn (im 2 ngày, đã nguội, không có Zalo)
/// để nhân viên gọi, rồi bấm kết quả. Kết quả cuộc gọi ghi vào nhật ký chăm sóc của khách (loại "Gọi điện").
/// </summary>
public sealed class CallListService(
    OutreachRepository outreach,
    ContactRepository contacts,
    ChannelConnectionRepository connections,
    AuditLogRepository audit,
    IFieldEncryptor encryptor,
    TimeProvider time)
{
    public const int MaxRows = 200;

    private const int MaxTextLength = 160;

    public async Task<IReadOnlyList<CallListEntry>> ListAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        var rows = await outreach.ListCallListAsync(tenantId, MaxRows, cancellationToken);
        var nicks = (await connections.ListAsync(tenantId, cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        var cold = rows.Where(r => r.Target.State == OutreachState.Cold && r.ConversationId is not null).Select(r => r.ConversationId!.Value).ToList();
        var lastTexts = await outreach.LastCustomerMessagesAsync(tenantId, cold, cancellationToken);

        // Danh sách có SĐT đầy đủ → ghi ai xem, lúc nào (CLAUDE.md mục 7).
        audit.Add(tenantId, userId, "care.call_list_viewed", $"{rows.Count} người");
        await outreach.SaveChangesAsync(cancellationToken);

        return rows.Select(r =>
        {
            var leads = ContactView.LeadFields(r.Contact, encryptor);
            var text = r.ConversationId is { } cid && lastTexts.TryGetValue(cid, out var enc) ? Clip(encryptor.Decrypt(enc)) : null;
            return new CallListEntry(
                r.Target.Id,
                r.Contact.Id,
                ContactView.Name(r.Contact, leads),
                r.Contact.PhoneEnc is null ? leads.GetValueOrDefault("phone") : encryptor.Decrypt(r.Contact.PhoneEnc),
                CustomerSalutation.Resolve(r.Contact.Salutation),
                r.Target.State,
                r.Origin,
                r.ReferrerName,
                r.Target.ConnectionId,
                r.Target.ConnectionId is { } nick ? nicks.GetValueOrDefault(nick) : null,
                r.ConversationId,
                r.Target.FirstMessageAt,
                r.Target.FriendRequestSentAt,
                r.LastCustomerMessageAt,
                text,
                r.Target.UpdatedAt,
                r.CallAttempts,
                r.LastCallAt);
        }).ToList();
    }

    public Task<(int Total, int Warm)> CountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        outreach.CountCallListAsync(tenantId, cancellationToken);

    /// <summary>
    /// Nhân viên bấm kết quả cuộc gọi: Quan tâm → về "Đang tư vấn", tầng khách lên Quan tâm; Không quan tâm → ngừng hẳn (không bao giờ
    /// nhắn chủ động nữa, tầng khách Không tiềm năng); Không nghe máy → giữ trong danh sách, đếm lại 30 ngày trước khi lưu trữ.
    /// </summary>
    /// <param name="result">interested | not_interested | no_answer</param>
    /// <param name="note">Ghi chú thêm của nhân viên (tùy chọn).</param>
    public async Task RecordResultAsync(Guid tenantId, Guid? userId, Guid targetId, string result, string? note, CancellationToken cancellationToken)
    {
        if (!OutreachLifecycle.CallResults.Contains(result, StringComparer.Ordinal))
        {
            throw new InvalidInputException("Kết quả cuộc gọi phải là interested, not_interested hoặc no_answer.");
        }

        var target = await outreach.GetTargetAsync(tenantId, targetId, cancellationToken) ?? throw new NotFoundException();
        if (!OutreachLifecycle.CallStates.Contains(target.State))
        {
            throw new ConflictException(
                "Số này không còn trong danh sách cần gọi (khách vừa nhắn lại, đã đặt lịch hoặc đã có người ghi kết quả). Tải lại trang.",
                "call_list_stale");
        }

        var contact = await contacts.GetAsync(tenantId, target.ContactId, cancellationToken) ?? throw new NotFoundException();
        var now = time.GetUtcNow();
        var label = result switch
        {
            OutreachLifecycle.Interested => "Quan tâm",
            OutreachLifecycle.NotInterested => "Không quan tâm",
            _ => "Không nghe máy",
        };
        contacts.AddNote(tenantId, new ContactNote
        {
            ContactId = contact.Id,
            AuthorUserId = userId,
            Kind = ContactNoteKind.Call,
            ContentEnc = encryptor.Encrypt(string.IsNullOrWhiteSpace(note) ? $"Gọi điện: {label}" : $"Gọi điện: {label}. {note.Trim()}"),
            HappenedOn = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime),
        });

        switch (result)
        {
            case OutreachLifecycle.Interested:
                // Đếm lại 14 ngày: không chat / đặt lịch gì thì quay về danh sách gọi dưới dạng "đã nguội".
                target.State = OutreachState.Replied;
                target.RepliedAt = now;
                target.LastError = null;
                target.NextActionAt = null;
                LeadRules.Promote(contact, LeadStatus.Interested, now);
                break;
            case OutreachLifecycle.NotInterested:
                target.State = OutreachState.Stopped;
                target.LastError = OutreachLifecycle.NotInterested;
                target.NextActionAt = null;
                contact.ProactiveOptOutAt ??= now;
                contact.ProactiveOptOutSource ??= "staff_call";
                if (contact.LeadStatus != LeadStatus.Won)
                {
                    contact.LeadStatus = LeadStatus.Lost;
                    contact.LeadStatusChangedAt = now;
                }

                break;
            default:
                target.UpdatedAt = now; // có người xử lý: đếm lại 30 ngày trước khi lưu trữ
                break;
        }

        audit.Add(tenantId, userId, "care.call_result", $"{targetId} {result}");
        await outreach.SaveChangesAsync(cancellationToken);
    }

    private static string Clip(string text)
    {
        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return oneLine.Length > MaxTextLength ? oneLine[..MaxTextLength] + "…" : oneLine;
    }
}
