using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <param name="Reason">invalid | duplicate | opted_out | already_chatting | already_queued</param>
public sealed record OutreachSkip(string Phone, string Reason);

/// <param name="EtaDays">Ước tính số ngày để hàng đợi của nick này đi hết (theo giới hạn hiện tại).</param>
public sealed record OutreachBatchResult(Guid BatchId, int Accepted, IReadOnlyList<OutreachSkip> Skipped, int EtaDays);

/// <summary>Tóm tắt hàng đợi một nick hôm nay.</summary>
public sealed record OutreachSummary(Guid ConnectionId, string? NickName, PacingLimits Limits, PacingUsage Usage, int Queued, int EtaDays, OutreachLevel Level);

/// <summary>
/// Ô dán SĐT hàng loạt + màn hình theo dõi (docs/ZALO-PERSONAL-PLAN.md mục 6.9). Chỉ người đã có quan hệ với DN: DN chọn nguồn và
/// PHẢI xác nhận; ai xác nhận, lúc nào được lưu. Số đã từ chối nhận tin, đang chat, đang trong hàng đợi → bỏ qua kèm lý do.
/// </summary>
public sealed class OutreachService(
    OutreachRepository outreach,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    ContactRepository contacts,
    AuditLogRepository audit,
    MembershipRepository memberships,
    PersonalSendLimiter limiter,
    IFieldEncryptor encryptor,
    IPhoneHasher hasher,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time)
{
    public const int MaxPhonesPerBatch = 1000;

    public static readonly string[] Origins = ["referral", "inquiry", "customer"];

    public async Task<OutreachBatchResult> CreateBatchAsync(
        Guid tenantId,
        Guid userId,
        string phonesText,
        string origin,
        string? note,
        string? referrerName,
        string? referrerPhone,
        Guid? connectionId,
        bool attested,
        CancellationToken cancellationToken)
    {
        if (!attested)
        {
            throw new InvalidInputException("Cần xác nhận các số này là người đã liên hệ, đã dùng dịch vụ hoặc được khách giới thiệu.");
        }

        if (!Origins.Contains(origin, StringComparer.Ordinal))
        {
            throw new InvalidInputException("Nguồn phải là referral, inquiry hoặc customer.");
        }

        var connection = await ResolveConnectionAsync(tenantId, connectionId, cancellationToken);
        var now = time.GetUtcNow();

        // Mỗi dòng: SĐT, có thể kèm tên sau dấu phẩy.
        var lines = phonesText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaxPhonesPerBatch)
        {
            throw new InvalidInputException($"Mỗi lần dán tối đa {MaxPhonesPerBatch} số.");
        }

        var skipped = new List<OutreachSkip>();
        var parsed = new List<(string Phone, string Hash, string? Name)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var parts = line.Split([',', ';', '\t'], 2, StringSplitOptions.TrimEntries);
            var raw = parts[0];
            if (PhoneNumber.Normalize(raw) is not { } phone)
            {
                skipped.Add(new(Short(raw, 30), "invalid"));
                continue;
            }

            if (!seen.Add(phone))
            {
                skipped.Add(new(PhoneNumber.Mask(phone), "duplicate"));
                continue;
            }

            var name = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? Short(parts[1], 100) : null;
            parsed.Add((phone, hasher.Hash(phone), name));
        }

        var existing = await outreach.ContactsByPhoneHashAsync(tenantId, parsed.Select(p => p.Hash).ToList(), cancellationToken);
        var existingIds = existing.Values.Select(c => c.Id).ToList();
        var chatting = await outreach.ContactsChattingAsync(tenantId, existingIds, cancellationToken);
        var queued = await outreach.ContactsInActiveOutreachAsync(tenantId, existingIds, cancellationToken);

        Guid? referrerId = null;
        if (PhoneNumber.Normalize(referrerPhone) is { } refPhone)
        {
            referrerId = (await outreach.ContactsByPhoneHashAsync(tenantId, [hasher.Hash(refPhone)], cancellationToken)).Values.FirstOrDefault()?.Id;
        }

        var batch = outreach.AddBatch(tenantId, new OutreachBatch
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = connection.Id,
            Origin = origin,
            NoteEnc = string.IsNullOrWhiteSpace(note) ? null : encryptor.Encrypt(note.Trim()),
            ReferrerContactId = referrerId,
            ReferrerName = string.IsNullOrWhiteSpace(referrerName) ? null : Short(referrerName, 100),
            AttestedByUserId = userId,
            AttestedAt = now,
        });

        var accepted = 0;
        foreach (var (phone, hash, name) in parsed)
        {
            existing.TryGetValue(hash, out var contact);
            if (contact is not null)
            {
                if (contact.ProactiveOptOutAt is not null)
                {
                    skipped.Add(new(PhoneNumber.Mask(phone), "opted_out"));
                    continue;
                }

                if (chatting.Contains(contact.Id))
                {
                    skipped.Add(new(PhoneNumber.Mask(phone), "already_chatting"));
                    continue;
                }

                if (queued.Contains(contact.Id))
                {
                    skipped.Add(new(PhoneNumber.Mask(phone), "already_queued"));
                    continue;
                }

                contact.Origin ??= origin;
                contact.ReferredByContactId ??= referrerId;
                contact.DisplayName ??= name;
            }
            else
            {
                contact = contacts.AddContact(tenantId, new Contact
                {
                    Channel = ChannelKind.Phone,
                    ExternalUserId = hash,
                    DisplayName = name,
                    PhoneEnc = encryptor.Encrypt(phone),
                    PhoneHash = hash,
                    Origin = origin,
                    ReferredByContactId = referrerId,
                });
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                contacts.AddNote(tenantId, new ContactNote
                {
                    ContactId = contact.Id,
                    AuthorUserId = userId,
                    Kind = ContactNoteKind.Note,
                    ContentEnc = encryptor.Encrypt($"Ghi chú cho AI: {note.Trim()}"),
                    HappenedOn = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime),
                });
            }

            outreach.AddTarget(tenantId, new OutreachTarget
            {
                BatchId = batch.Id,
                ContactId = contact.Id,
                ConnectionId = connection.Id,
                AssignedUserId = userId,
                State = OutreachState.Queued,
                NextActionAt = now,
            });
            accepted++;
        }

        batch.AcceptedCount = accepted;
        batch.SkippedCount = skipped.Count;
        audit.Add(tenantId, userId, "outreach.batch_created", $"{batch.Id} origin={origin} accepted={accepted} skipped={skipped.Count}");
        await outreach.SaveChangesAsync(cancellationToken);

        var account = await personal.GetAccountAsync(tenantId, connection.Id, cancellationToken);
        var eta = account is null ? 0 : Eta(await outreach.CountQueuedAsync(tenantId, connection.Id, cancellationToken), PersonalPacing.Effective(account, options.Value, now));
        return new(batch.Id, accepted, skipped, eta);
    }

    public async Task<IReadOnlyList<OutreachSummary>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var list = new List<OutreachSummary>();
        var names = (await connections.ListAsync(tenantId, cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        foreach (var account in await personal.ListAccountsAsync(tenantId, cancellationToken))
        {
            var limits = PersonalPacing.Effective(account, options.Value, now);
            var queued = await outreach.CountQueuedAsync(tenantId, account.ConnectionId, cancellationToken);
            list.Add(new OutreachSummary(
                account.ConnectionId,
                names.GetValueOrDefault(account.ConnectionId),
                limits,
                await limiter.UsageAsync(account.ConnectionId, cancellationToken),
                queued,
                Eta(queued, limits),
                account.OutreachLevel));
        }

        return list;
    }

    /// <summary>Đổi nhân viên phụ trách và/hoặc dừng chăm sóc nhiều người một lần. Trả số người thực sự thay đổi.</summary>
    public async Task<int> UpdateTargetsAsync(
        Guid tenantId, Guid? userId, IReadOnlyCollection<Guid> ids, Guid? assignedUserId, bool stop, CancellationToken cancellationToken)
    {
        if (ids.Count is 0 or > 500)
        {
            throw new InvalidInputException("Chọn từ 1 đến 500 người.");
        }

        // Nhân viên phụ trách phải thuộc đúng DN (không gán người của DN khác).
        if (assignedUserId is { } assignee && await memberships.GetAsync(tenantId, assignee, cancellationToken) is null)
        {
            throw new InvalidInputException("Nhân viên phụ trách không thuộc doanh nghiệp.");
        }

        var targets = await outreach.GetTargetsAsync(tenantId, ids, cancellationToken);
        var changed = 0;
        foreach (var t in targets)
        {
            var touched = false;
            if (assignedUserId is not null && t.AssignedUserId != assignedUserId)
            {
                t.AssignedUserId = assignedUserId;
                touched = true;
            }

            if (stop && OutreachRepository.ActiveStates.Contains(t.State))
            {
                t.State = OutreachState.Stopped;
                t.NextActionAt = null;
                touched = true;
            }

            changed += touched ? 1 : 0;
        }

        audit.Add(tenantId, userId, stop ? "outreach.stopped" : "outreach.assigned", $"{changed} người");
        await outreach.SaveChangesAsync(cancellationToken);
        return changed;
    }

    public async Task<IReadOnlyList<OutreachBatch>> ListBatchesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await outreach.ListBatchesAsync(tenantId, cancellationToken);

    /// <summary>Nick phụ trách: nick chỉ định, hoặc nick Zalo cá nhân đầu tiên đang hoạt động.</summary>
    private async Task<ChannelConnection> ResolveConnectionAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken)
    {
        var list = await connections.ListAsync(tenantId, cancellationToken);
        var personalNicks = list.Where(c => c.Channel == ChannelKind.ZaloPersonal).ToList();
        var chosen = connectionId is { } id ? personalNicks.FirstOrDefault(c => c.Id == id) : personalNicks.FirstOrDefault(c => c.Status == ConnectionStatus.Active) ?? personalNicks.FirstOrDefault();
        return chosen ?? throw new ConflictException("Chưa kết nối nick Zalo cá nhân nào. Kết nối ở trang Kênh trước.", "zalo_personal_no_nick");
    }

    /// <summary>Số ngày để đi hết hàng đợi: mỗi người cần 1 lượt "người mới" (tin đầu hoặc lời mời).</summary>
    private static int Eta(int queued, PacingLimits limits)
    {
        var perDay = Math.Max(1, limits.NewContactsPerDay);
        return queued == 0 ? 0 : (int)Math.Ceiling(queued / (double)perDay);
    }

    private static string Short(string value, int max) => value.Length > max ? value[..max] : value;
}
