using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Một dòng màn hình "Đang chăm sóc" (người + hồ sơ khách + hội thoại + lịch hẹn sắp tới).</summary>
public sealed record OutreachRow(OutreachTarget Target, Contact Contact, Guid? ConversationId, DateTimeOffset? NextAppointmentAt);

/// <summary>Một dòng danh sách "Cần gọi điện" (người + hồ sơ + nguồn của đợt dán + hội thoại + các lần nhân viên đã gọi).</summary>
/// <param name="CallAttempts">Số lần nhân viên đã ghi kết quả gọi kể từ lúc dán số này.</param>
public sealed record CallListRow(
    OutreachTarget Target,
    Contact Contact,
    string Origin,
    string? ReferrerName,
    Guid? ConversationId,
    DateTimeOffset? LastCustomerMessageAt,
    int CallAttempts,
    DateTimeOffset? LastCallAt);

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
    /// Người của nick đã thôi được nhắn (Cần gọi điện, Đã nguội, Lưu trữ, lời mời hết hạn) theo uid — lần dán gần nhất, không có lần dán mới
    /// hơn (được track). Khách tự nhắn lại / chấp nhận kết bạn muộn → đưa về đúng trạng thái.
    /// </summary>
    public Task<OutreachTarget?> FindRevivableByUidAsync(Guid tenantId, Guid connectionId, string uid, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId && t.ZaloUid == uid
                        && OutreachLifecycle.RevivableStates.Contains(t.State)
                        && !Db.OutreachTargets.Any(o => o.TenantId == tenantId && o.ContactId == t.ContactId && o.CreatedAt > t.CreatedAt))
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Số lần bot đã chạm (nhắn / mời được) từng hồ sơ và lần gần nhất — để chặn dán lại số đã liên hệ (90 ngày, tối đa 2 lần).
    /// </summary>
    public async Task<Dictionary<Guid, (int Count, DateTimeOffset Last)>> TouchesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> contactIds, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (contactIds.Count == 0)
        {
            return [];
        }

        var rows = await Db.OutreachTargets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && contactIds.Contains(t.ContactId) && (t.FirstMessageAt != null || t.FriendRequestSentAt != null))
            .Select(t => new { t.ContactId, t.FirstMessageAt, t.FriendRequestSentAt })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ContactId).ToDictionary(g => g.Key, g => (g.Count(), g.Max(r => Latest(r.FirstMessageAt, r.FriendRequestSentAt))));

        static DateTimeOffset Latest(DateTimeOffset? a, DateTimeOffset? b) =>
            a is { } x && (b is not { } y || x >= y) ? x : b.GetValueOrDefault();
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
    public async Task<IReadOnlyList<OutreachTarget>> ListStaleInvitesAsync(Guid tenantId, DateTimeOffset invitedBefore, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.State == OutreachState.Invited && t.FriendRequestSentAt < invitedBefore)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Đã nhận tin bên mình mà im 24 giờ tính từ tin CUỐI bên mình gửi (tin chào / tin giới thiệu / nhân viên nhắn) → "Cần gọi điện"
    /// (được track). Còn việc sắp chạy (giới thiệu ngay sau khi khách chấp nhận kết bạn) thì đợi việc đó xong.
    /// </summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListSilentAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var before = now - OutreachLifecycle.SilentAfter;
        var soon = now + OutreachLifecycle.Imminent;
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.State == OutreachState.Messaged && t.FirstMessageAt < before
                        && (t.NextActionAt == null || t.NextActionAt > soon)
                        && !Db.Conversations.Any(c => c.TenantId == tenantId && c.ContactId == t.ContactId && c.LastMessageAt >= before))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Đang tư vấn mà khách im 14 ngày (mọi kênh) → "Đã nguội" (được track).</summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListGoneColdAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var before = now - OutreachLifecycle.ColdAfter;
        var soon = now + OutreachLifecycle.Imminent;
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && t.State == OutreachState.Replied
                        && (t.NextActionAt == null || t.NextActionAt > soon)
                        && (t.RepliedAt == null || t.RepliedAt < before)
                        && !Db.Conversations.Any(c => c.TenantId == tenantId && c.ContactId == t.ContactId && c.LastCustomerMessageAt >= before))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Có lịch hẹn (trừ đã hủy) hoặc hóa đơn (trừ đã hủy) tạo SAU lúc dán số → "Đã thành khách" (được track). Lịch hẹn cũ của khách quen
    /// trước lúc dán không tính.
    /// </summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListConvertedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && OutreachLifecycle.ConvertibleStates.Contains(t.State)
                        && (Db.Appointments.Any(a => a.TenantId == tenantId && a.ContactId == t.ContactId && a.CreatedAt >= t.CreatedAt
                                                     && a.Status != AppointmentStatus.Cancelled)
                            || Db.Invoices.Any(i => i.TenantId == tenantId && i.ContactId == t.ContactId && i.CreatedAt >= t.CreatedAt
                                                    && i.Status != InvoiceStatus.Void)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Trong danh sách gọi điện 30 ngày không ai xử lý (không đổi gì, kể cả ghi "không nghe máy") → "Lưu trữ" (được track).</summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListToArchiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var before = now - OutreachLifecycle.ArchiveAfter;
        return await Db.OutreachTargets
            .Where(t => t.TenantId == tenantId && OutreachLifecycle.CallStates.Contains(t.State) && t.UpdatedAt < before)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Danh sách "Cần gọi điện": đã nguội trước (từng tư vấn), rồi im lặng, rồi không có Zalo; trong nhóm mới nhất trước. Bỏ người đã từ
    /// chối nhận tin, người có lần dán mới hơn, người vừa nhắn lại (mọi kênh), người đã có lịch hẹn mới.
    /// </summary>
    public async Task<IReadOnlyList<CallListRow>> ListCallListAsync(Guid tenantId, int take, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await (from t in CallListQuery(tenantId)
                          join c in Db.Contacts.AsNoTracking() on t.ContactId equals c.Id
                          join b in Db.OutreachBatches.AsNoTracking() on t.BatchId equals b.Id
                          where c.TenantId == tenantId && b.TenantId == tenantId
                          orderby (t.State == OutreachState.Cold ? 0 : t.State == OutreachState.NoReply ? 1 : 2), t.UpdatedAt descending
                          select new
                          {
                              Target = t,
                              Contact = c,
                              b.Origin,
                              b.ReferrerName,
                              ConversationId = Db.Conversations.Where(v => v.TenantId == tenantId && v.ContactId == c.Id && v.ConnectionId == t.ConnectionId)
                                  .OrderByDescending(v => v.CreatedAt).Select(v => (Guid?)v.Id).FirstOrDefault(),
                              LastCustomerMessageAt = Db.Conversations.Where(v => v.TenantId == tenantId && v.ContactId == c.Id)
                                  .Max(v => v.LastCustomerMessageAt),
                              Calls = Db.ContactNotes.Count(n => n.TenantId == tenantId && n.ContactId == c.Id && n.Kind == ContactNoteKind.Call
                                                                 && n.CreatedAt >= t.CreatedAt),
                              LastCallAt = Db.ContactNotes.Where(n => n.TenantId == tenantId && n.ContactId == c.Id && n.Kind == ContactNoteKind.Call
                                                                       && n.CreatedAt >= t.CreatedAt)
                                  .Max(n => (DateTimeOffset?)n.CreatedAt),
                          })
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new CallListRow(
            r.Target, r.Contact, r.Contact.Origin ?? r.Origin, r.ReferrerName, r.ConversationId, r.LastCustomerMessageAt, r.Calls, r.LastCallAt)).ToList();
    }

    /// <summary>Tin gần nhất của khách trong từng hội thoại (nội dung mã hóa) — để nhân viên biết khách đã nguội từng hỏi gì.</summary>
    public async Task<Dictionary<Guid, string>> LastCustomerMessagesAsync(Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (conversationIds.Count == 0)
        {
            return [];
        }

        var rows = await Db.Messages.AsNoTracking()
            .Where(m => m.TenantId == tenantId && conversationIds.Contains(m.ConversationId) && m.Sender == MessageSender.Customer)
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(m => m.CreatedAt).Select(m => new { m.ConversationId, m.ContentEnc }).First())
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.ConversationId, r => r.ContentEnc);
    }

    /// <summary>Số người trong danh sách "Cần gọi điện" và số đã nguội (từng tư vấn) — cho huy hiệu menu, trang Tổng quan, Telegram.</summary>
    public async Task<(int Total, int Warm)> CountCallListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var byState = await CallListQuery(tenantId)
            .GroupBy(t => t.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return (byState.Sum(r => r.Count), byState.Where(r => r.State == OutreachState.Cold).Sum(r => r.Count));
    }

    /// <summary>
    /// CHỈ môi trường dev (nút "Tua nhanh thời gian" của khung Giả lập): lùi mọi mốc giờ chăm sóc của một nick — người trong hàng đợi,
    /// hội thoại và tin nhắn của nick — đi <paramref name="by"/>, như thể ngày đã trôi qua. Cập nhật hàng loạt (bỏ qua kiểm tra tenant của
    /// SaveChanges) nhưng luôn lọc đúng tenant + nick. Giờ hẹn việc kế tiếp (mời kết bạn, giới thiệu) cũng lùi → thành "tới hạn"; nơi gọi
    /// chạy job hàng đợi rồi job dọn dẹp ngay sau đó, như thể các việc đó đã chạy trong lúc thời gian trôi.
    /// </summary>
    public async Task ShiftTimeForDevAsync(Guid tenantId, Guid connectionId, TimeSpan by, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        await Db.OutreachTargets.Where(t => t.TenantId == tenantId && t.ConnectionId == connectionId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CreatedAt, t => t.CreatedAt - by)
            .SetProperty(t => t.UpdatedAt, t => t.UpdatedAt - by)
            .SetProperty(t => t.FirstMessageAt, t => t.FirstMessageAt - by)
            .SetProperty(t => t.FriendRequestSentAt, t => t.FriendRequestSentAt - by)
            .SetProperty(t => t.RepliedAt, t => t.RepliedAt - by)
            .SetProperty(t => t.NextActionAt, t => t.NextActionAt - by), cancellationToken);
        var conversations = Db.Conversations.Where(c => c.TenantId == tenantId && c.ConnectionId == connectionId);
        await Db.Messages.Where(m => m.TenantId == tenantId && conversations.Any(c => c.Id == m.ConversationId))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, m => m.CreatedAt - by), cancellationToken);
        await conversations.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.CreatedAt, c => c.CreatedAt - by)
            .SetProperty(c => c.LastMessageAt, c => c.LastMessageAt - by)
            .SetProperty(c => c.LastCustomerMessageAt, c => c.LastCustomerMessageAt - by), cancellationToken);
    }

    /// <summary>Số người cần gọi theo từng nick (trang Zalo cá nhân, trang nick).</summary>
    public async Task<Dictionary<Guid, int>> CountCallListByConnectionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await CallListQuery(tenantId)
            .Where(t => t.ConnectionId != null)
            .GroupBy(t => t.ConnectionId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(r => r.Key, r => r.Count, cancellationToken);
    }

    private IQueryable<OutreachTarget> CallListQuery(Guid tenantId) =>
        Db.OutreachTargets.AsNoTracking()
            .Where(t => t.TenantId == tenantId && OutreachLifecycle.CallStates.Contains(t.State)
                        && !Db.Contacts.Any(c => c.TenantId == tenantId && c.Id == t.ContactId && c.ProactiveOptOutAt != null)
                        && !Db.OutreachTargets.Any(o => o.TenantId == tenantId && o.ContactId == t.ContactId && o.CreatedAt > t.CreatedAt)
                        && !Db.Conversations.Any(v => v.TenantId == tenantId && v.ContactId == t.ContactId && v.LastCustomerMessageAt > t.UpdatedAt)
                        && !Db.Appointments.Any(a => a.TenantId == tenantId && a.ContactId == t.ContactId && a.CreatedAt >= t.CreatedAt
                                                     && a.Status != AppointmentStatus.Cancelled));

    /// <summary>Người đã nhận tin đầu / lời mời mà hồ sơ khách đã từ chối nhận tin ("hủy") — để đánh dấu từ chối (được track).</summary>
    public async Task<IReadOnlyList<OutreachTarget>> ListOptedOutActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await (from t in Db.OutreachTargets
                      join c in Db.Contacts on t.ContactId equals c.Id
                      where t.TenantId == tenantId && c.TenantId == tenantId && c.ProactiveOptOutAt != null
                            && (ActiveStates.Contains(t.State) || t.State == OutreachState.Replied
                                || OutreachLifecycle.CallStates.Contains(t.State) || t.State == OutreachState.Archived)
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
                                               && (r.State is OutreachState.Messaged or OutreachState.NoReply or OutreachState.Archived
                                                   || (r.State is OutreachState.Replied or OutreachState.Cold or OutreachState.Converted
                                                       && r.FriendRequestSentAt < r.RepliedAt)))));
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
        OutreachState[] reached =
        [
            OutreachState.Invited, OutreachState.Friend, OutreachState.Messaged, OutreachState.Replied, OutreachState.NoReply,
            OutreachState.Converted, OutreachState.Cold, OutreachState.Archived,
        ];
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

    /// <summary>Tenant còn người cần job dọn dẹp để mắt tới (đang chạy, đang tư vấn, trong danh sách gọi điện).</summary>
    public async Task<IReadOnlyList<Guid>> TenantsWithOutreachAsync(CancellationToken cancellationToken) =>
        await db.OutreachTargets.IgnoreQueryFilters()
            .Where(t => OutreachRepository.ActiveStates.Contains(t.State) || t.State == OutreachState.Replied || OutreachLifecycle.CallStates.Contains(t.State))
            .Select(t => t.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
}
