using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Khách (hồ sơ, tầng tiềm năng, nhãn), ghi chú nhân viên và gợi ý chăm sóc của một tenant (CLAUDE.md M6).</summary>
public sealed class ContactRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    /// <summary>Giới hạn số khách tải một lần: tên/SĐT đã mã hóa nên tìm kiếm làm trong bộ nhớ sau khi giải mã.</summary>
    public const int MaxContacts = 5000;

    /// <summary>
    /// Khách của tenant (không gồm khách chat thử nếu <paramref name="includeTest"/> = false), mới tương tác trước.
    /// Lọc theo tầng/nhãn ở database; tìm theo tên/SĐT ở tầng gọi (dữ liệu mã hóa).
    /// </summary>
    public async Task<IReadOnlyList<Contact>> ListAsync(
        Guid tenantId,
        LeadStatus? status,
        string? tag,
        bool includeTest,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (!includeTest)
        {
            query = query.Where(c => c.Channel != ChannelKind.Webchat);
        }

        if (status is { } s)
        {
            query = query.Where(c => c.LeadStatus == s);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(c => c.Tags.Contains(tag));
        }

        return await query
            .OrderByDescending(c => c.LastCustomerMessageAt ?? c.CreatedAt)
            .Take(MaxContacts)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Số khách theo từng tầng (thanh lọc trên trang Khách hàng).</summary>
    public async Task<Dictionary<LeadStatus, int>> CountByStatusAsync(Guid tenantId, bool includeTest, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (!includeTest)
        {
            query = query.Where(c => c.Channel != ChannelKind.Webchat);
        }

        return await query.GroupBy(c => c.LeadStatus).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    }

    /// <summary>Mọi nhãn đang dùng (gợi ý khi gắn nhãn, bộ lọc).</summary>
    public async Task<IReadOnlyList<string>> ListTagsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var tags = await Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId).SelectMany(c => c.Tags).Distinct().ToListAsync(cancellationToken);
        return tags.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<Contact?> GetAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Contacts.Where(c => c.TenantId == tenantId && c.Id == contactId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Hội thoại của khách, mới trước.</summary>
    public async Task<IReadOnlyList<Conversation>> ListConversationsAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Conversations.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Hội thoại gần nhất của khách (được track) — nơi gắn gợi ý chăm sóc.</summary>
    public Task<Conversation?> LatestConversationAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Conversations
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public ContactNote AddNote(Guid tenantId, ContactNote note)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(note);
        note.TenantId = tenantId;
        if (note.Id == Guid.Empty)
        {
            note.Id = Guid.CreateVersion7();
        }

        Db.ContactNotes.Add(note);
        return note;
    }

    /// <summary>Ghi chú của khách, mới trước (ngày xảy ra, rồi lúc tạo).</summary>
    public async Task<IReadOnlyList<ContactNote>> ListNotesAsync(Guid tenantId, Guid contactId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.ContactNotes.AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.ContactId == contactId)
            .OrderByDescending(n => n.HappenedOn).ThenByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Được track.</summary>
    public Task<ContactNote?> GetNoteAsync(Guid tenantId, Guid noteId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.ContactNotes.Where(n => n.TenantId == tenantId && n.Id == noteId).FirstOrDefaultAsync(cancellationToken);
    }

    public void RemoveNote(Guid tenantId, ContactNote note)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(note);
        Db.ContactNotes.Remove(note);
    }

    /// <summary>Gợi ý đang mở của khách (tối đa 1, được track).</summary>
    public Task<CareSuggestion?> GetOpenSuggestionAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.CareSuggestions
            .Where(s => s.TenantId == tenantId && s.ContactId == contactId && s.Status == CareSuggestionStatus.Open)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Được track.</summary>
    public Task<CareSuggestion?> GetSuggestionAsync(Guid tenantId, Guid suggestionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.CareSuggestions.Where(s => s.TenantId == tenantId && s.Id == suggestionId).FirstOrDefaultAsync(cancellationToken);
    }

    public CareSuggestion AddSuggestion(Guid tenantId, CareSuggestion suggestion)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(suggestion);
        suggestion.TenantId = tenantId;
        if (suggestion.Id == Guid.Empty)
        {
            suggestion.Id = Guid.CreateVersion7();
        }

        Db.CareSuggestions.Add(suggestion);
        return suggestion;
    }

    /// <summary>
    /// Gợi ý chăm sóc kèm khách. Mặc định: còn mở, sắp hết hạn nhắn trước → nóng → mới.
    /// </summary>
    /// <param name="filter">mine | hot | expiring | all</param>
    public async Task<IReadOnlyList<(CareSuggestion Suggestion, Contact Contact)>> ListSuggestionsAsync(
        Guid tenantId,
        CareSuggestionStatus status,
        string filter,
        Guid? userId,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query =
            from s in Db.CareSuggestions.AsNoTracking()
            join c in Db.Contacts.AsNoTracking() on s.ContactId equals c.Id
            where s.TenantId == tenantId && c.TenantId == tenantId && s.Status == status
            select new { s, c };

        query = filter switch
        {
            "mine" => query.Where(x => x.s.AssignedUserId == userId),
            "hot" => query.Where(x => x.s.Temperature == CareTemperature.Hot),
            "expiring" => query.Where(x => x.s.MessagingDeadline != null && x.s.MessagingDeadline < now.AddDays(2)),
            _ => query,
        };

        var rows = await query
            .OrderBy(x => x.s.MessagingDeadline == null)
            .ThenBy(x => x.s.MessagingDeadline)
            .ThenByDescending(x => x.s.Temperature)
            .ThenByDescending(x => x.s.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return rows.Select(x => (x.s, x.c)).ToList();
    }

    /// <summary>Gợi ý của một khách (mọi trạng thái), mới trước — cho trang hồ sơ khách.</summary>
    public async Task<IReadOnlyList<CareSuggestion>> ListSuggestionsForContactAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.CareSuggestions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.ContactId == contactId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Số gợi ý đang mở (badge menu).</summary>
    public Task<int> CountOpenSuggestionsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.CareSuggestions.CountAsync(s => s.TenantId == tenantId && s.Status == CareSuggestionStatus.Open, cancellationToken);
    }
}
