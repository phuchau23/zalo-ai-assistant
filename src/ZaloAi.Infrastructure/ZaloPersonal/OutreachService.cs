using Microsoft.Extensions.Options;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <param name="Reason">invalid | duplicate | opted_out | already_chatting | already_queued | contacted_recently | contacted_twice</param>
/// <param name="RetryAfter">contacted_recently: từ lúc này dán lại được (90 ngày sau lần liên hệ gần nhất).</param>
public sealed record OutreachSkip(string Phone, string Reason, DateTimeOffset? RetryAfter = null);

/// <summary>Một nick nhận bao nhiêu người trong lần dán này.</summary>
/// <param name="EtaDays">Dự kiến số ngày để nick này đi hết hàng chờ (gồm người đang chờ từ trước).</param>
public sealed record OutreachAllocation(Guid ConnectionId, string? NickName, int Assigned, int QueuedAfter, int EtaDays);

/// <param name="BatchId">null khi chỉ xem trước (<paramref name="DryRun"/>).</param>
/// <param name="EtaDays">Ước tính số ngày để hàng đợi đi hết (nick chậm nhất trong các nick được giao).</param>
/// <param name="DryRun">true = chỉ xem trước cách chia, chưa lưu gì.</param>
public sealed record OutreachBatchResult(
    Guid? BatchId, int Accepted, IReadOnlyList<OutreachSkip> Skipped, int EtaDays, IReadOnlyList<OutreachAllocation> Allocations, bool DryRun);

/// <summary>Một việc gần đây của nick (người vừa được nhắn, mời, trả lời...).</summary>
public sealed record OutreachActivity(Guid TargetId, string? Name, string? PhoneMasked, OutreachState State, DateTimeOffset At);

/// <summary>Tóm tắt một nick: giới hạn và đã dùng hôm nay, hàng chờ, số người theo từng bước, việc gần đây.</summary>
/// <param name="NextSendAt">Đang ngoài khung giờ được nhắn của DN → lúc hàng đợi chạy lại (vd 08:00 sáng mai); trong khung giờ → null.</param>
/// <param name="StateCounts">Số người theo trạng thái (mọi đợt, kể cả đã kết thúc).</param>
/// <param name="Booked">Số người nick đã liên hệ được mà đang có lịch hẹn sắp tới.</param>
/// <param name="HoldingForReplies">Hôm nay nick đã dùng 70% lượt tin → tạm ngưng nhắn người mới, dành lượt trả lời khách đang chat.</param>
public sealed record OutreachSummary(
    Guid ConnectionId,
    string? NickName,
    PacingLimits Limits,
    PacingUsage Usage,
    int Queued,
    int EtaDays,
    OutreachLevel Level,
    DateTimeOffset? NextSendAt,
    IReadOnlyDictionary<OutreachState, int> StateCounts,
    int Booked,
    IReadOnlyList<OutreachActivity> Recent,
    bool HoldingForReplies = false);

/// <summary>
/// Ô dán SĐT hàng loạt + màn hình theo dõi (docs/ZALO-PERSONAL-PLAN.md mục 6.9). Chỉ người đã có quan hệ với DN: DN chọn nguồn và
/// PHẢI xác nhận; ai xác nhận, lúc nào được lưu. Số đã từ chối nhận tin, đang chat, đang trong hàng đợi → bỏ qua kèm lý do.
/// Giao cho một nick, hoặc chia tự động cho nhiều nick (<see cref="OutreachAllocator"/>, chủ dự án chốt 2026-11-15); xem trước được.
/// </summary>
public sealed class OutreachService(
    OutreachRepository outreach,
    ZaloPersonalRepository personal,
    ChannelConnectionRepository connections,
    ContactRepository contacts,
    AuditLogRepository audit,
    MembershipRepository memberships,
    HandoffSettingsRepository handoffSettings,
    PersonalSendLimiter limiter,
    IFieldEncryptor encryptor,
    IPhoneHasher hasher,
    IOptions<ZaloPersonalOptions> options,
    TimeProvider time)
{
    public const int MaxPhonesPerBatch = 1000;

    /// <summary>Số việc gần đây trả về cho mỗi nick.</summary>
    public const int RecentPerNick = 5;

    public static readonly string[] Origins = ["referral", "inquiry", "customer"];

    /// <param name="connectionId">Giao hết cho nick này (khi <paramref name="distribute"/> = false). null = nick đang chạy đầu tiên.</param>
    /// <param name="distribute">Chia tự động cho nhiều nick đang chạy.</param>
    /// <param name="connectionIds">Khi chia: chỉ chia cho các nick này (null/rỗng = mọi nick đang chạy).</param>
    /// <param name="dryRun">Chỉ xem trước: kiểm tra số + cách chia, không lưu gì (không cần xác nhận nguồn).</param>
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
        CancellationToken cancellationToken,
        bool distribute = false,
        IReadOnlyCollection<Guid>? connectionIds = null,
        bool dryRun = false)
    {
        if (!attested && !dryRun)
        {
            throw new InvalidInputException("Cần xác nhận các số này là người đã liên hệ, đã dùng dịch vụ hoặc được khách giới thiệu.");
        }

        if (!Origins.Contains(origin, StringComparer.Ordinal))
        {
            throw new InvalidInputException("Nguồn phải là referral, inquiry hoặc customer.");
        }

        var now = time.GetUtcNow();
        var nicks = distribute
            ? await RunningNicksAsync(tenantId, connectionIds, now, cancellationToken)
            : [await SingleNickAsync(tenantId, connectionId, now, cancellationToken)];

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
        var touches = await outreach.TouchesAsync(tenantId, existingIds, cancellationToken);
        var familiar = distribute ? await outreach.LastConnectionByContactAsync(tenantId, existingIds, cancellationToken) : [];

        // Lượt 1: lọc người nhận được (chưa ghi gì). Số đã liên hệ mà chưa từng trả lời: chặn dán lại 90 ngày, tối đa 2 lần mỗi số
        // (danh sách tạm khóa, chủ dự án chốt 2026-11-25) — dán đi dán lại cùng số là kiểu spam làm nick bị khóa.
        var accepted = new List<(string Phone, string Hash, string? Name, Contact? Contact)>();
        foreach (var (phone, hash, name) in parsed)
        {
            existing.TryGetValue(hash, out var contact);
            DateTimeOffset? retryAfter = null;
            var reason = contact switch
            {
                null => null,
                _ when contact.ProactiveOptOutAt is not null => "opted_out",
                _ when chatting.Contains(contact.Id) => "already_chatting",
                _ when queued.Contains(contact.Id) => "already_queued",
                _ when touches.TryGetValue(contact.Id, out var touch) => OutreachLifecycle.PasteBlock(touch.Count, touch.Last, now, out retryAfter),
                _ => null,
            };
            if (reason is not null)
            {
                skipped.Add(new(PhoneNumber.Mask(phone), reason, retryAfter));
                continue;
            }

            accepted.Add((phone, hash, name, contact));
        }

        // Lượt 2: chia cho các nick (một nick = giao hết cho nick đó).
        var preferred = accepted.Select(a => a.Contact is { } c && familiar.TryGetValue(c.Id, out var nick) ? nick : (Guid?)null).ToList();
        var (assignments, summary) = OutreachAllocator.Allocate(preferred, nicks.Select(n => n.Capacity).ToList());
        var names = nicks.ToDictionary(n => n.Capacity.ConnectionId, n => n.Name);
        var allocations = summary
            .Select(a => new OutreachAllocation(a.ConnectionId, names.GetValueOrDefault(a.ConnectionId), a.Assigned, a.QueuedAfter, a.EtaDays))
            .ToList();
        var eta = summary.Where(a => a.Assigned > 0).Select(a => a.EtaDays).DefaultIfEmpty(0).Max();
        if (dryRun)
        {
            return new(null, accepted.Count, skipped, eta, allocations, DryRun: true);
        }

        Guid? referrerId = null;
        if (PhoneNumber.Normalize(referrerPhone) is { } refPhone)
        {
            referrerId = (await outreach.ContactsByPhoneHashAsync(tenantId, [hasher.Hash(refPhone)], cancellationToken)).Values.FirstOrDefault()?.Id;
        }

        var usedNicks = summary.Where(a => a.Assigned > 0).Select(a => a.ConnectionId).ToList();
        var batch = outreach.AddBatch(tenantId, new OutreachBatch
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = usedNicks.Count == 1 ? usedNicks[0] : nicks.Count == 1 ? nicks[0].Capacity.ConnectionId : null, // chia nhiều nick → null
            Origin = origin,
            NoteEnc = string.IsNullOrWhiteSpace(note) ? null : encryptor.Encrypt(note.Trim()),
            ReferrerContactId = referrerId,
            ReferrerName = string.IsNullOrWhiteSpace(referrerName) ? null : Short(referrerName, 100),
            AttestedByUserId = userId,
            AttestedAt = now,
        });

        // Lượt 3: ghi hồ sơ + người trong hàng đợi.
        for (var i = 0; i < accepted.Count; i++)
        {
            var (phone, hash, name, contact) = accepted[i];
            if (contact is not null)
            {
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
                ConnectionId = assignments[i],
                AssignedUserId = userId,
                State = OutreachState.Queued,
                NextActionAt = now,
            });
        }

        batch.AcceptedCount = accepted.Count;
        batch.SkippedCount = skipped.Count;
        var split = string.Join(",", allocations.Where(a => a.Assigned > 0).Select(a => $"{a.ConnectionId:N}:{a.Assigned}"));
        audit.Add(tenantId, userId, "outreach.batch_created", $"{batch.Id} origin={origin} accepted={accepted.Count} skipped={skipped.Count} nicks={split}");
        await outreach.SaveChangesAsync(cancellationToken);
        return new(batch.Id, accepted.Count, skipped, eta, allocations, DryRun: false);
    }

    public async Task<IReadOnlyList<OutreachSummary>> SummaryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = await handoffSettings.GetAsync(tenantId, cancellationToken);
        DateTimeOffset? nextSendAt = CareDecision.InSendWindow(settings, now) ? null : CareDecision.NextSendTime(settings, now);
        var counts = (await outreach.StateCountsAsync(tenantId, cancellationToken)).ToLookup(r => r.ConnectionId);
        var booked = await outreach.BookedCountsAsync(tenantId, now, cancellationToken);
        var list = new List<OutreachSummary>();
        var names = (await connections.ListAsync(tenantId, cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        foreach (var account in await personal.ListAccountsAsync(tenantId, cancellationToken))
        {
            var limits = PersonalPacing.Effective(account, options.Value, now);
            var queued = await outreach.CountQueuedAsync(tenantId, account.ConnectionId, cancellationToken);
            var recent = await outreach.RecentAsync(tenantId, account.ConnectionId, RecentPerNick, cancellationToken);
            var usage = await limiter.UsageAsync(account.ConnectionId, cancellationToken);
            list.Add(new OutreachSummary(
                account.ConnectionId,
                names.GetValueOrDefault(account.ConnectionId),
                limits,
                usage,
                queued,
                OutreachAllocator.Eta(queued, limits.NewContactsPerDay),
                account.OutreachLevel,
                nextSendAt,
                counts[account.ConnectionId].ToDictionary(r => r.State, r => r.Count),
                booked.GetValueOrDefault(account.ConnectionId),
                recent.Select(r => new OutreachActivity(
                    r.Target.Id,
                    r.Contact.DisplayName,
                    r.Contact.PhoneEnc is null ? null : PhoneNumber.Mask(encryptor.Decrypt(r.Contact.PhoneEnc)),
                    r.Target.State,
                    r.Target.UpdatedAt)).ToList(),
                queued > 0 && usage.Messages >= OutreachLifecycle.NewOutreachMessageCap(limits.MessagesPerDay)));
        }

        return list;
    }

    /// <summary>
    /// Đổi nhân viên phụ trách, dừng chăm sóc, và/hoặc chuyển sang nick khác nhiều người một lần. Trả số người thực sự thay đổi.
    /// Chuyển nick chỉ áp dụng cho người CHƯA được liên hệ (đang chờ lượt): người đã được nick A nhắn/mời giữ nick A, để khách không thấy
    /// hai nick lạ cùng nhắn (chủ dự án chốt 2026-11-15).
    /// </summary>
    public async Task<int> UpdateTargetsAsync(
        Guid tenantId,
        Guid? userId,
        IReadOnlyCollection<Guid> ids,
        Guid? assignedUserId,
        bool stop,
        CancellationToken cancellationToken,
        Guid? moveToConnectionId = null)
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

        // Nick đích phải là nick Zalo cá nhân của đúng DN này.
        if (moveToConnectionId is { } target && !await IsPersonalNickAsync(tenantId, target, cancellationToken))
        {
            throw new InvalidInputException("Nick đích không phải nick Zalo cá nhân của doanh nghiệp.");
        }

        var targets = await outreach.GetTargetsAsync(tenantId, ids, cancellationToken);
        var changed = 0;
        var moved = 0;
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

            if (!stop && moveToConnectionId is { } to && t.State == OutreachState.Queued && t.ConnectionId != to)
            {
                t.ConnectionId = to;
                touched = true;
                moved++;
            }

            changed += touched ? 1 : 0;
        }

        var action = stop ? "outreach.stopped" : moveToConnectionId is not null ? "outreach.moved" : "outreach.assigned";
        audit.Add(tenantId, userId, action, moveToConnectionId is { } dest ? $"{moved} người → nick {dest:N}" : $"{changed} người");
        await outreach.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>
    /// Chia lại những người ĐANG CHỜ của một nick cho các nick đang chạy khác (nick bị mất kết nối, bị phanh, hay DN muốn nghỉ nick đó).
    /// Trả số người đã chuyển theo từng nick nhận.
    /// </summary>
    public async Task<IReadOnlyList<OutreachAllocation>> RedistributeAsync(Guid tenantId, Guid? userId, Guid fromConnectionId, CancellationToken cancellationToken)
    {
        if (!await IsPersonalNickAsync(tenantId, fromConnectionId, cancellationToken))
        {
            throw new NotFoundException("Không tìm thấy nick.");
        }

        var now = time.GetUtcNow();
        var others = (await RunningNicksAsync(tenantId, null, now, cancellationToken, required: false))
            .Where(n => n.Capacity.ConnectionId != fromConnectionId)
            .ToList();
        if (others.Count == 0)
        {
            throw new ConflictException("Không còn nick nào khác đang chạy để nhận những người này.", "zalo_personal_no_running_nick");
        }

        var waiting = await outreach.QueuedOfConnectionAsync(tenantId, fromConnectionId, cancellationToken);
        var (assignments, summary) = OutreachAllocator.Allocate(waiting.Select(_ => (Guid?)null).ToList(), others.Select(n => n.Capacity).ToList());
        for (var i = 0; i < waiting.Count; i++)
        {
            waiting[i].ConnectionId = assignments[i];
        }

        var names = others.ToDictionary(n => n.Capacity.ConnectionId, n => n.Name);
        var result = summary
            .Select(a => new OutreachAllocation(a.ConnectionId, names.GetValueOrDefault(a.ConnectionId), a.Assigned, a.QueuedAfter, a.EtaDays))
            .ToList();
        audit.Add(tenantId, userId, "outreach.redistributed", $"{waiting.Count} người từ nick {fromConnectionId:N}");
        await outreach.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<OutreachBatch>> ListBatchesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await outreach.ListBatchesAsync(tenantId, cancellationToken);

    private sealed record Nick(NickCapacity Capacity, string? Name);

    /// <summary>Nick phụ trách: nick chỉ định (dù đang dừng — hàng chờ đợi nick chạy lại), hoặc nick Zalo cá nhân đầu tiên đang hoạt động.</summary>
    private async Task<Nick> SingleNickAsync(Guid tenantId, Guid? connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var list = await connections.ListAsync(tenantId, cancellationToken);
        var personalNicks = list.Where(c => c.Channel == ChannelKind.ZaloPersonal).ToList();
        var chosen = connectionId is { } id ? personalNicks.FirstOrDefault(c => c.Id == id) : personalNicks.FirstOrDefault(c => c.Status == ConnectionStatus.Active) ?? personalNicks.FirstOrDefault();
        if (chosen is null)
        {
            throw new ConflictException("Chưa kết nối nick Zalo cá nhân nào. Kết nối ở trang Zalo cá nhân trước.", "zalo_personal_no_nick");
        }

        var account = await personal.GetAccountAsync(tenantId, chosen.Id, cancellationToken);
        var perDay = account is null ? 1 : PersonalPacing.Effective(account, options.Value, now).NewContactsPerDay;
        return new Nick(new NickCapacity(chosen.Id, perDay, await outreach.CountQueuedAsync(tenantId, chosen.Id, cancellationToken)), chosen.Name);
    }

    /// <summary>
    /// Nick được chia số: đang kết nối (Active), phanh tự động không ở mức "tạm dừng", hôm nay còn nhận người mới. Như "health check" của
    /// bộ chia tải: quầy đóng thì không xếp khách vào.
    /// </summary>
    private async Task<List<Nick>> RunningNicksAsync(
        Guid tenantId, IReadOnlyCollection<Guid>? only, DateTimeOffset now, CancellationToken cancellationToken, bool required = true)
    {
        var list = await connections.ListAsync(tenantId, cancellationToken);
        var accounts = (await personal.ListAccountsAsync(tenantId, cancellationToken)).ToDictionary(a => a.ConnectionId);
        var result = new List<Nick>();
        foreach (var c in list.Where(c => c.Channel == ChannelKind.ZaloPersonal && c.Status == ConnectionStatus.Active))
        {
            if ((only is { Count: > 0 } && !only.Contains(c.Id)) || !accounts.TryGetValue(c.Id, out var account) || account.OutreachLevel == OutreachLevel.Paused)
            {
                continue;
            }

            var limits = PersonalPacing.Effective(account, options.Value, now);
            if (limits.NewContactsPerDay <= 0)
            {
                continue;
            }

            result.Add(new Nick(new NickCapacity(c.Id, limits.NewContactsPerDay, await outreach.CountQueuedAsync(tenantId, c.Id, cancellationToken)), c.Name));
        }

        if (required && result.Count == 0)
        {
            throw new ConflictException(
                "Không có nick nào đang chạy để chia số (nick phải đang kết nối và không bị tạm dừng). Kiểm tra trang Zalo cá nhân.",
                "zalo_personal_no_running_nick");
        }

        return result;
    }

    private async Task<bool> IsPersonalNickAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        (await connections.ListAsync(tenantId, cancellationToken)).Any(c => c.Id == connectionId && c.Channel == ChannelKind.ZaloPersonal);

    private static string Short(string value, int max) => value.Length > max ? value[..max] : value;
}
