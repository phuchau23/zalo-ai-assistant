using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>Tin nhắn mẫu của DN (hộp thư gõ "/" để chèn).</summary>
public sealed class QuickReplyRepository(AppDbContext db, ITenantContext tenantContext) : TenantScopedRepository(db, tenantContext)
{
    public async Task<IReadOnlyList<QuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.QuickReplies.AsNoTracking()
            .Where(q => q.TenantId == tenantId)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Shortcut)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.QuickReplies.CountAsync(q => q.TenantId == tenantId, cancellationToken);
    }

    /// <summary>Được track. Không thuộc tenant → null.</summary>
    public Task<QuickReply?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.QuickReplies.FirstOrDefaultAsync(q => q.TenantId == tenantId && q.Id == id, cancellationToken);
    }

    public Task<bool> ShortcutTakenAsync(Guid tenantId, string shortcut, Guid? exceptId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return Db.QuickReplies.AnyAsync(q => q.TenantId == tenantId && q.Shortcut == shortcut && q.Id != exceptId, cancellationToken);
    }

    public QuickReply Add(Guid tenantId, QuickReply reply)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(reply);
        reply.TenantId = tenantId;
        if (reply.Id == Guid.Empty)
        {
            reply.Id = Guid.CreateVersion7();
        }

        Db.QuickReplies.Add(reply);
        return reply;
    }

    public void Remove(Guid tenantId, QuickReply reply)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.TenantId != tenantId)
        {
            throw new TenantIsolationException("Tin nhắn mẫu không thuộc tenant hiện tại.");
        }

        Db.QuickReplies.Remove(reply);
    }
}
