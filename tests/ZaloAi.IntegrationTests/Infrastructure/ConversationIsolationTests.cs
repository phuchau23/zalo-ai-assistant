using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.IntegrationTests.Infrastructure;

/// <summary>Cô lập tenant cho khách, hội thoại, tin nhắn, chi phí AI (skill tenant-safe-feature bước 7).</summary>
[Collection(PostgresGroup.Name)]
public sealed class ConversationIsolationTests(PostgresFixture db)
{
    private sealed record Scope(TenantContext Context, AppDbContext Db) : IAsyncDisposable
    {
        public ConversationRepository Conversations => new(Db, Context);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private Scope As(Guid tenantId)
    {
        var context = PostgresFixture.CreateTenantContext(tenantId);
        return new Scope(context, db.CreateDbContext(context));
    }

    /// <summary>Tạo khách + hội thoại + 1 tin khách + 1 tin bot trả lời + 1 dòng chi phí.</summary>
    private async Task<(Guid ConversationId, Guid CustomerMessageId)> SeedAsync(Guid tenantId, string externalUserId)
    {
        await using var s = As(tenantId);
        var contact = await s.Conversations.GetOrAddContactAsync(tenantId, ChannelKind.Webchat, externalUserId, "Khách", CancellationToken.None);
        var conversation = s.Conversations.AddConversation(tenantId, new Conversation { ContactId = contact.Id, Channel = ChannelKind.Webchat, IsTest = true });
        var customer = s.Conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.In,
            Sender = MessageSender.Customer,
            ContentEnc = "enc-in",
            ExternalMessageId = $"ext-{externalUserId}",
        });
        s.Conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.Out,
            Sender = MessageSender.Bot,
            ContentEnc = "enc-out",
            ReplyToMessageId = customer.Id,
        });
        s.Conversations.AddUsage(tenantId, new UsageRecord { Kind = UsageKind.Chat, Provider = "fake", Model = "fake-chat", ConversationId = conversation.Id });
        await s.Db.SaveChangesAsync();
        return (conversation.Id, customer.Id);
    }

    [Fact]
    public async Task Tenant_B_cannot_read_tenant_A_conversations_messages_or_usage()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Chat A");
        var (tenantB, _) = await db.CreateTenantAsync("Chat B");
        var (conversationA, messageA) = await SeedAsync(tenantA, "khach-a");

        await using var asB = As(tenantB);
        (await asB.Conversations.GetConversationAsync(tenantB, conversationA, CancellationToken.None)).ShouldBeNull();
        (await asB.Conversations.GetMessageAsync(tenantB, messageA, CancellationToken.None)).ShouldBeNull();
        (await asB.Conversations.FindReplyAsync(tenantB, messageA, CancellationToken.None)).ShouldBeNull();
        (await asB.Conversations.ListMessagesAsync(tenantB, conversationA, null, CancellationToken.None)).ShouldBeEmpty();
        (await asB.Conversations.HasBotReplyAsync(tenantB, conversationA, CancellationToken.None)).ShouldBeFalse();
        (await asB.Db.Contacts.AnyAsync()).ShouldBeFalse();
        (await asB.Db.UsageRecords.AnyAsync()).ShouldBeFalse();

        await Should.ThrowAsync<TenantIsolationException>(() => asB.Conversations.GetConversationAsync(tenantA, conversationA, CancellationToken.None));

        await using var asA = As(tenantA);
        (await asA.Conversations.ListMessagesAsync(tenantA, conversationA, null, CancellationToken.None)).Count.ShouldBe(2);
        (await asA.Conversations.FindReplyAsync(tenantA, messageA, CancellationToken.None)).ShouldNotBeNull();
        (await asA.Conversations.HasBotReplyAsync(tenantA, conversationA, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Same_external_user_in_two_tenants_are_different_contacts()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Contact A");
        var (tenantB, _) = await db.CreateTenantAsync("Contact B");
        await SeedAsync(tenantA, "zalo-user-1");
        await SeedAsync(tenantB, "zalo-user-1");

        await using var asB = As(tenantB);
        (await asB.Db.Contacts.CountAsync()).ShouldBe(1);
        var contact = await asB.Conversations.GetOrAddContactAsync(tenantB, ChannelKind.Webchat, "zalo-user-1", null, CancellationToken.None);
        contact.TenantId.ShouldBe(tenantB);
        asB.Db.Entry(contact).State.ShouldBe(EntityState.Unchanged); // tìm thấy, không tạo mới
    }

    [Fact]
    public async Task Bot_can_reply_only_once_per_customer_message()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Reply once");
        var (conversationId, messageId) = await SeedAsync(tenantId, "khach-1");

        await using var s = As(tenantId);
        s.Conversations.AddMessage(tenantId, new Message
        {
            ConversationId = conversationId,
            Direction = MessageDirection.Out,
            Sender = MessageSender.Bot,
            ContentEnc = "enc-out-2",
            ReplyToMessageId = messageId,
        });
        var ex = await Should.ThrowAsync<DbUpdateException>(() => s.Db.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Moving_conversation_to_another_tenant_is_blocked()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Move chat A");
        var (tenantB, _) = await db.CreateTenantAsync("Move chat B");
        var (conversationId, _) = await SeedAsync(tenantA, "khach-move");

        await using var asA = As(tenantA);
        var conversation = (await asA.Conversations.GetConversationAsync(tenantA, conversationId, CancellationToken.None)).ShouldNotBeNull();
        conversation.TenantId = tenantB;
        await Should.ThrowAsync<TenantIsolationException>(() => asA.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_conversation_deletes_messages_and_keeps_usage()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Delete chat");
        var (conversationId, _) = await SeedAsync(tenantId, "khach-xoa");

        await using (var s = As(tenantId))
        {
            var conversation = (await s.Conversations.GetConversationAsync(tenantId, conversationId, CancellationToken.None)).ShouldNotBeNull();
            s.Conversations.Remove(tenantId, conversation);
            await s.Db.SaveChangesAsync();
        }

        await using var check = As(tenantId);
        (await check.Db.Messages.AnyAsync()).ShouldBeFalse();
        var usage = (await check.Db.UsageRecords.ToListAsync()).ShouldHaveSingleItem(); // chi phí vẫn giữ để tính tiền
        usage.ConversationId.ShouldBeNull();
    }
}
