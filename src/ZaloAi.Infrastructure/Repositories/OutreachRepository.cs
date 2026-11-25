using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Một dòng màn hình "Đang chăm sóc" (người + hồ sơ khách + hội thoại + lịch hẹn sắp tới).</summary>
public sealed record OutreachRow(OutreachTarget Target, Contact Contact, Guid? ConversationId, DateTimeOffset? NextAppointmentAt);

/// <summary>Hàng đợi chăm sóc qua Zalo cá nhân (docs/ZALO-PERSONAL-PLAN.md mục 6.9) — dữ liệu theo tenant.</summary>
public sealed class OutreachRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Trạng thái còn "đang chạy" (một người chỉ một dòng đang chạy — unique index).</summary>
    public static readonly OutreachState[] ActiveStates = [OutreachState.Queued, OutreachState.Invited, OutreachState.Friend, OutreachState.Messaged];

    public OutreachBatch AddBatch(Guid tenantId, OutreachBatch batch)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(batch);
        batch.TenantId = tenantId;
        if (batch.Id == Guid.Empty)
        {
            batch.Id = Guid.CreateVersion7();
        }

        Db.OutreachBatches.Add(batch);
        return batch;
    }

    public OutreachTarget AddTarget(Guid tenantId, OutreachTarget target)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(target);
        target.TenantId = tenantId;
        if (target.Id == Guid.Empty)
        {
            target.Id = Guid.CreateVersion7(); // thêm nhiều dòng trước một lần SaveChanges → phải có Id riêng ngay
        }

        Db.OutreachTargets.Add(target);
        return target;
    }

    /// <summary>Khách của tenant theo dấu vân tay SĐT (được track).</summary>
    public async Task<Dictionary<string, Contact>> ContactsByPhoneHashAsync(Guid tenantId, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Contacts
            .Where(c => c.TenantId == tenantId && c.PhoneHash != null && hashes.Contains(c.PhoneHash))
            .ToDictionaryAsync(c => c.PhoneHash!, StringComparer.Ordinal, cancellationToken);
    }

    /// <summary>Hồ sơ khách đã có hội thoại qua kênh nhắn tin (đang/đã chat với DN) trong số các id.</summary>
    public async Task<HashSet<Guid>> ContactsChattingAsync(Guid tenantId, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var ids = await Db.Conversations
            .Where(c => c.TenantId == tenantId && contactIds.Contains(c.ContactId) && !c.IsTest && c.Channel != ChannelKind.Phone && c.LastCustomerMessageAt != null)
            .Select(c => c.ContactId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return [.. ids];
    }

    public async Task<HashSet<Guid>> ContactsInActiveOutreachAsync(Guid tenantId, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var ids = await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && contactIds.Contains(t.ContactId) && ActiveStates.Contains(t.State))
            .Select(t => t.ContactId)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }

    /// <summary>Các đợt dán SĐT, mới nhất trước (tối đa 100) — để lọc màn hình theo dõi.</summary>
    public async Task<IReadOnlyList<OutreachBatch>> ListBatchesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachBatches.AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .OrderByDescending(b => b.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    public Task<OutreachBatch?> GetBatchAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.OutreachBatches.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId && b.Id == batchId, cancellationToken);
    }

    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<OutreachTarget?> GetTargetAsync(Guid tenantId, Guid targetId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.OutreachTargets.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Id == targetId, cancellationToken);
    }

    /// <summary>Được track. Chỉ các id thuộc tenant.</summary>
    public async Task<IReadOnlyList<OutreachTarget>> GetTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets.Where(t => t.TenantId == tenantId && ids.Contains(t.Id)).ToListAsync(cancellationToken);
    }

    /// <summary>Người đang được nick này chăm sóc theo uid (được track) — khi khách chấp nhận kết bạn / trả lời.</summary>
    public Task<OutreachTarget?> FindActiveByUidAsync(Guid tenantId, Guid connectionId, string uid, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId && t.ZaloUid == uid && ActiveStates.Contains(t.State))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Trạng thái job chăm sóc còn việc khi tới NextActionAt (Messaged: mời kết bạn sau tin chào / giới thiệu khi khách vừa chấp nhận;
    /// Replied: mời kết bạn sau khi khách trả lời / cảm ơn khi chấp nhận).
    /// </summary>
    public static readonly OutreachState[] DueStates = [OutreachState.Queued, OutreachState.Friend, OutreachState.Messaged, OutreachState.Replied];

    /// <summary>Người đã trả lời gần nhất của nick theo uid (được track) — khi khách chấp nhận lời mời gửi sau lúc trả lời.</summary>
    public Task<OutreachTarget?> FindRepliedByUidAsync(Guid tenantId, Guid connectionId, string uid, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId && t.ZaloUid == uid && t.State == OutreachState.Replied)
            .OrderByDescending(t => t.RepliedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Tới lượt xử lý (được track). Người đã trả lời / đã kết bạn đứng TRƯỚC người mới trong hàng: lượt mời kết bạn trong ngày ưu tiên
    /// người đã nói chuyện (gần như chắc chấp nhận), người mới chỉ dùng phần còn lại (chủ dự án chốt 2026-11-15).
    /// </summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListDueAsync(Guid tenantId, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId
                        && DueStates.Contains(t.State)
                        && t.NextActionAt != null && t.NextActionAt <= now)
            .OrderBy(t => t.State == OutreachState.Queued ? 1 : 0).ThenBy(t => t.NextActionAt).ThenBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Nhóm dịch vụ / gói tiêu biểu (nhiều mục nhất trước) để tin chủ động nói "bên mình hỗ trợ được gì" — không có giá. DN chưa chia
    /// nhóm → tên vài dịch vụ.
    /// </summary>
    public async Task<IReadOnlyList<string>> OfferingsAsync(Guid tenantId, int max, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var items = Db.CatalogItems.AsNoTracking().Where(c => c.TenantId == tenantId && c.IsActive && c.Kind != CatalogKind.Product);
        var groups = await items.Where(c => c.GroupName != null && c.GroupName != "")
            .GroupBy(c => c.GroupName!)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key)
            .Take(max)
            .ToListAsync(cancellationToken);
        return groups.Count > 0
            ? groups
            : await items.OrderBy(c => c.Name).Select(c => c.Name).Take(max).ToListAsync(cancellationToken);
    }

    /// <summary>Lời mời quá hạn / tin đầu không ai trả lời (được track).</summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListStaleAsync(Guid tenantId, DateTimeOffset invitedBefore, DateTimeOffset messagedBefore, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId
                        && ((t.State == OutreachState.Invited && t.FriendRequestSentAt < invitedBefore)
                            || (t.State == OutreachState.Messaged && t.FirstMessageAt < messagedBefore)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Người đã nhận tin đầu / lời mời mà hồ sơ khách đã từ chối nhận tin ("hủy") — để đánh dấu từ chối (được track).</summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListOptedOutActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await (from t in Db.OutreachTargets
                      join c in Db.Contacts on t.ContactId equals c.Id
                      where t.TenantId == tenantId && c.TenantId == tenantId && c.ProactiveOptOutAt != null
                            && (ActiveStates.Contains(t.State) || t.State == OutreachState.Replied)
                      select t)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Số liệu 7 ngày của một nick cho phanh tự động.</summary>
    public async Task<(int Invites, int Accepted, int FirstMessages, int Negatives)> RatesAsync(
        Guid tenantId, Guid connectionId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId
                        && ((t.FriendRequestSentAt != null && t.FriendRequestSentAt >= since) || (t.FirstMessageAt != null && t.FirstMessageAt >= since)))
            .Select(t => new
            {
                t.State,
                t.FriendRequestSentAt,
                t.FirstMessageAt,
                t.RepliedAt,
                IsFriend = Db.ZaloPeers.Any(p => p.TenantId == tenantId && p.ConnectionId == connectionId && p.ExternalUserId == t.ZaloUid && p.IsFriend),
            })
            .ToListAsync(cancellationToken);
        var invites = rows.Count(r => r.FriendRequestSentAt >= since);

        // Đã là bạn → chấp nhận. Lời mời ĐI TRƯỚC tin đầu (khách chặn tin người lạ: tin đầu chỉ gửi được sau khi chấp nhận) → có tin / trả lời
        // về sau nghĩa là đã chấp nhận. Lời mời gửi SAU tin chào (2026-11-25) hoặc sau khi trả lời → chỉ tính khi đã là bạn.
        var accepted = rows.Count(r => r.FriendRequestSentAt >= since
                                       && (r.IsFriend
                                           || r.State == OutreachState.Friend
                                           || ((r.FirstMessageAt == null || r.FriendRequestSentAt < r.FirstMessageAt)
                                               && (r.State is OutreachState.Messaged or OutreachState.NoReply
                                                   || (r.State == OutreachState.Replied && r.FriendRequestSentAt < r.RepliedAt)))));
        var firstMessages = rows.Count(r => r.FirstMessageAt >= since);
        var negatives = rows.Count(r => r.State == OutreachState.Declined);
        return (invites, accepted, firstMessages, negatives);
    }

    public async Task<int> CountQueuedAsync(Guid tenantId, Guid? connectionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets.CountAsync(
            t => t.TenantId == tenantId && (connectionId == null || t.ConnectionId == connectionId) && (t.State == OutreachState.Queued || t.State == OutreachState.Friend),
            cancellationToken);
    }

    /// <summary>
    /// Nick gần nhất từng nhắn/mời mỗi hồ sơ (đợt chăm sóc trước hoặc hội thoại Zalo cá nhân) — để khi chia danh sách, khách quen nick nào
    /// thì giữ nick đó.
    /// </summary>
    public async Task<Dictionary<Guid, Guid>> LastConnectionByContactAsync(Guid tenantId, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (contactIds.Count == 0)
        {
            return [];
        }

        var fromTargets = await Db.OutreachTargets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && contactIds.Contains(t.ContactId) && t.ConnectionId != null && t.State != OutreachState.Queued)
            .Select(t => new { t.ContactId, ConnectionId = t.ConnectionId!.Value, At = t.UpdatedAt })
            .ToListAsync(cancellationToken);
        var fromConversations = await Db.Conversations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && contactIds.Contains(c.ContactId) && c.Channel == ChannelKind.ZaloPersonal && c.ConnectionId != null)
            .Select(c => new { c.ContactId, ConnectionId = c.ConnectionId!.Value, At = c.CreatedAt })
            .ToListAsync(cancellationToken);
        return fromTargets.Concat(fromConversations)
            .GroupBy(r => r.ContactId)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.At)!.ConnectionId);
    }

    /// <summary>Số người theo trạng thái của từng nick (cả các trạng thái đã kết thúc).</summary>
    public async Task<IReadOnlyList<(Guid ConnectionId, OutreachState State, int Count)>> StateCountsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await Db.OutreachTargets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ConnectionId != null)
            .GroupBy(t => new { ConnectionId = t.ConnectionId!.Value, t.State })
            .Select(g => new { g.Key.ConnectionId, g.Key.State, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.ConnectionId, r.State, r.Count)).ToList();
    }

    /// <summary>Số người nick đã liên hệ được mà đang có lịch hẹn sắp tới (thành quả dễ thấy nhất của việc chăm sóc).</summary>
    public async Task<Dictionary<Guid, int>> BookedCountsAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        OutreachState[] reached = [OutreachState.Invited, OutreachState.Friend, OutreachState.Messaged, OutreachState.Replied, OutreachState.NoReply];
        return await Db.OutreachTargets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ConnectionId != null && reached.Contains(t.State)
                        && Db.Appointments.Any(a => a.TenantId == tenantId && a.ContactId == t.ContactId && a.StartAt >= now
                                                    && a.Status != AppointmentStatus.Cancelled))
            .GroupBy(t => t.ConnectionId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(r => r.Key, r => r.Count, cancellationToken);
    }

    /// <summary>Việc gần nhất của một nick (người vừa được nhắn, mời, trả lời...) — người còn chờ lượt không tính.</summary>
    public async Task<IReadOnlyList<(OutreachTarget Target, Contact Contact)>> RecentAsync(
        Guid tenantId, Guid connectionId, int take, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await (from t in Db.OutreachTargets.AsNoTracking()
                          join c in Db.Contacts.AsNoTracking() on t.ContactId equals c.Id
                          where t.TenantId == tenantId && c.TenantId == tenantId && t.ConnectionId == connectionId && t.State != OutreachState.Queued
                          orderby t.UpdatedAt descending
                          select new { t, c })
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.t, r.c)).ToList();
    }

    /// <summary>Hồ sơ nick Zalo cá nhân đang được chăm sóc tới (đang chờ lượt) theo nick — để chuyển hàng chờ sang nick khác.</summary>
    public async Task<IReadOnlyList<OutreachTarget>> QueuedOfConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets.Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId && t.State == OutreachState.Queued)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Màn hình "Đang chăm sóc": mới nhất trước, tối đa 500 dòng.</summary>
    public async Task<IReadOnlyList<OutreachRow>> ListBoardAsync(
        Guid tenantId, OutreachState? state, Guid? connectionId, Guid? assignedUserId, Guid? batchId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.OutreachTargets.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (state is { } s)
        {
            query = query.Where(t => t.State == s);
        }

        if (connectionId is { } conn)
        {
            query = query.Where(t => t.ConnectionId == conn);
        }

        if (assignedUserId is { } user)
        {
            query = query.Where(t => t.AssignedUserId == user);
        }

        if (batchId is { } batch)
        {
            query = query.Where(t => t.BatchId == batch);
        }

        return await (from t in query
                      join c in Db.Contacts.AsNoTracking() on t.ContactId equals c.Id
                      where c.TenantId == tenantId
                      orderby t.UpdatedAt descending
                      select new OutreachRow(
                          t,
                          c,
                          Db.Conversations.Where(v => v.TenantId == tenantId && v.ContactId == c.Id && v.ConnectionId == t.ConnectionId)
                              .OrderByDescending(v => v.CreatedAt).Select(v => (Guid?)v.Id).FirstOrDefault(),
                          Db.Appointments.Where(a => a.TenantId == tenantId && a.ContactId == c.Id && a.StartAt >= DateTimeOffset.UtcNow
                                                      && a.Status != AppointmentStatus.Cancelled)
                              .OrderBy(a => a.StartAt).Select(a => (DateTimeOffset?)a.StartAt).FirstOrDefault()))
            .Take(500)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
/// Truy vấn HỆ THỐNG cho job chăm sóc (chạy cho mọi tenant): tenant nào đang có người tới lượt / cần dọn. IgnoreQueryFilters() CÓ CHỦ
/// ĐÍCH, chỉ trả id tenant; xử lý tiếp chạy trong job theo từng tenant với tenant context.
/// </summary>
public sealed class OutreachLookup(AppDbContext db)
{
    public async Task<IReadOnlyList<Guid>> TenantsWithDueAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await (from t in db.OutreachTargets.IgnoreQueryFilters()
               join tenant in db.Tenants.IgnoreQueryFilters() on t.TenantId equals tenant.Id
               where tenant.Status == TenantStatus.Active
                     && OutreachRepository.DueStates.Contains(t.State)
                     && t.NextActionAt != null && t.NextActionAt <= now
               select t.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> TenantsWithOutreachAsync(CancellationToken cancellationToken) =>
        await db.OutreachTargets.IgnoreQueryFilters()
            .Where(t => OutreachRepository.ActiveStates.Contains(t.State) || t.State == OutreachState.Replied)
            .Select(t => t.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
