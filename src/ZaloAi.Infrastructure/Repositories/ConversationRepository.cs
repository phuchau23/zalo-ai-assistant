using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Khách, hội thoại, tin nhắn của một tenant. Nội dung tin đã mã hóa ở tầng gọi (IFieldEncryptor).</summary>
public sealed class ConversationRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public async Task<Contact> GetOrAddContactAsync(
        Guid tenantId,
        ChannelKind channel,
        string externalUserId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var contact = await Db.Contacts
            .Where(c => c.TenantId == tenantId && c.Channel == channel && c.ExternalUserId == externalUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (contact is not null)
        {
            return contact;
        }

        contact = new Contact
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Channel = channel,
            ExternalUserId = externalUserId,
            DisplayName = displayName,
        };
        Db.Contacts.Add(contact);
        return contact;
    }

    public Task<Contact?> GetContactAsync(Guid tenantId, Guid contactId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Contacts.Where(c => c.TenantId == tenantId && c.Id == contactId).FirstOrDefaultAsync(cancellationToken);
    }

    public Conversation AddConversation(Guid tenantId, Conversation conversation)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.TenantId = tenantId;
        if (conversation.Id == Guid.Empty)
        {
            conversation.Id = Guid.CreateVersion7();
        }

        Db.Conversations.Add(conversation);
        return conversation;
    }

    /// <summary>Được track. Không thuộc tenant → null (API trả 404).</summary>
    public Task<Conversation?> GetConversationAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Conversations
            .Where(c => c.TenantId == tenantId && c.Id == conversationId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Hội thoại đang mở (không phải chat thử) của khách trên một kênh đã kết nối — được track.</summary>
    public Task<Conversation?> FindOpenConversationAsync(Guid tenantId, Guid contactId, Guid connectionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Conversations
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId && c.ConnectionId == connectionId
                        && c.Status == ConversationStatus.Open && !c.IsTest)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Tin theo mã của kênh (Zalo msg_id) — kênh gửi lại sự kiện cũ thì bỏ qua.</summary>
    public Task<bool> ExternalMessageExistsAsync(Guid tenantId, string externalMessageId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Messages.AnyAsync(m => m.TenantId == tenantId && m.ExternalMessageId == externalMessageId, cancellationToken);
    }

    public Message AddMessage(Guid tenantId, Message message)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(message);
        message.TenantId = tenantId;
        if (message.Id == Guid.Empty)
        {
            message.Id = Guid.CreateVersion7();
        }

        Db.Messages.Add(message);
        return message;
    }

    public Task<Message?> GetMessageAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Messages.Where(m => m.TenantId == tenantId && m.Id == messageId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Tin bot đã trả lời cho tin khách này (job chạy lại → không trả lời lần 2).</summary>
    public Task<Message?> FindReplyAsync(Guid tenantId, Guid customerMessageId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Messages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.ReplyToMessageId == customerMessageId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Tin của hội thoại theo thời gian. <paramref name="after"/>: chỉ lấy tin sau mốc (dùng khi đã có tóm tắt).</summary>
    public async Task<IReadOnlyList<Message>> ListMessagesAsync(
        Guid tenantId,
        Guid conversationId,
        DateTimeOffset? after,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Messages.AsNoTracking().Where(m => m.TenantId == tenantId && m.ConversationId == conversationId);
        if (after is not null)
        {
            query = query.Where(m => m.CreatedAt > after);
        }

        return await query.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Id tin khách mới nhất của hội thoại (khách nhắn liên tiếp → chỉ trả lời tin cuối).</summary>
    public Task<Guid> LatestCustomerMessageIdAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Messages
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId && m.Sender == MessageSender.Customer)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Select(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Tin ra còn chờ gửi của hội thoại (được track), cũ trước.</summary>
    public async Task<IReadOnlyList<Message>> ListPendingOutgoingAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Messages
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId
                        && m.Direction == MessageDirection.Out && m.DeliveryStatus == DeliveryStatus.Pending)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> HasBotReplyAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.Messages.AnyAsync(
            m => m.TenantId == tenantId && m.ConversationId == conversationId && m.Sender == MessageSender.Bot,
            cancellationToken);
    }

    public async Task<IReadOnlyList<Conversation>> ListConversationsAsync(Guid tenantId, bool isTest, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsTest == isTest)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Danh sách hộp thư: ca đang chờ người lên đầu (chờ lâu nhất trước), rồi theo tin mới nhất. Kèm khách và tin cuối.
    /// </summary>
    /// <param name="filter">attention | bot | human | mine | all</param>
    public async Task<IReadOnlyList<InboxRow>> ListInboxAsync(
        Guid tenantId,
        string filter,
        Guid? userId,
        bool includeTest,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == ConversationStatus.Open);
        if (!includeTest)
        {
            query = query.Where(c => !c.IsTest);
        }

        query = filter switch
        {
            "attention" => query.Where(c => c.NeedsAttentionSince != null),
            "bot" => query.Where(c => c.Mode == ConversationMode.Bot),
            "human" => query.Where(c => c.Mode == ConversationMode.Human),
            "mine" => query.Where(c => c.AssignedUserId == userId),
            _ => query,
        };

        var conversations = await query
            .OrderBy(c => c.NeedsAttentionSince == null)
            .ThenBy(c => c.NeedsAttentionSince)
            .ThenByDescending(c => c.LastMessageAt ?? c.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        var ids = conversations.Select(c => c.Id).ToList();
        var contactIds = conversations.Select(c => c.ContactId).Distinct().ToList();
        var contacts = await Db.Contacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var lastMessages = await Db.Messages.AsNoTracking()
            .Where(m => m.TenantId == tenantId && ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).First())
            .ToDictionaryAsync(m => m.ConversationId, cancellationToken);

        return conversations
            .Select(c => new InboxRow(c, contacts[c.ContactId], lastMessages.GetValueOrDefault(c.Id)))
            .ToList();
    }

    /// <summary>Tổng chi phí AI (USD) và số lần gọi của một hội thoại.</summary>
    public async Task<(decimal CostUsd, int Calls)> UsageForConversationAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var rows = await Db.UsageRecords
            .Where(u => u.TenantId == tenantId && u.ConversationId == conversationId)
            .GroupBy(_ => 1)
            .Select(g => new { Cost = g.Sum(u => u.CostUsd), Calls = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);
        return rows is null ? (0m, 0) : (rows.Cost, rows.Calls);
    }

    public void AddUsage(Guid tenantId, UsageRecord usage)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(usage);
        usage.TenantId = tenantId;
        if (usage.Id == Guid.Empty)
        {
            usage.Id = Guid.CreateVersion7();
        }

        Db.UsageRecords.Add(usage);
    }

    public void Remove(Guid tenantId, Conversation conversation)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.TenantId != tenantId)
        {
            throw new TenantIsolationException("Hội thoại không thuộc tenant hiện tại.");
        }

        Db.Conversations.Remove(conversation);
    }
}

/// <summary>Một dòng hộp thư: hội thoại + khách + tin cuối (có thể chưa có tin).</summary>
public sealed record InboxRow(Conversation Conversation, Contact Contact, Message? LastMessage);
