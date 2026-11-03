using System.Globalization;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Dashboard;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Hạn mức hội thoại/tháng (M6 đợt B4): khách đã nhắn trong tháng vẫn được bot trả lời, khách mới khi hết → chuyển nhân viên.</summary>
[Collection(PostgresGroup.Name)]
public sealed class QuotaTests(PostgresFixture db)
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>Hội thoại thật (kênh web, không phải Chat thử) + 1 tin khách lúc <paramref name="at"/>.</summary>
    private async Task<(Guid ConversationId, Guid MessageId)> CustomerAsync(Guid tenantId, DateTimeOffset at)
    {
        await using var context = db.CreateDbContext(tenantId);
        var contact = new Contact { Id = Guid.CreateVersion7(), TenantId = tenantId, Channel = ChannelKind.Webchat, ExternalUserId = Guid.NewGuid().ToString("N") };
        var conversation = new Conversation { Id = Guid.CreateVersion7(), TenantId = tenantId, ContactId = contact.Id, Channel = ChannelKind.Webchat };
        context.AddRange(contact, conversation);
        var messageId = AddCustomerMessage(context, tenantId, conversation.Id, at);
        await context.SaveChangesAsync();
        return (conversation.Id, messageId);
    }

    private Guid AddCustomerMessage(Microsoft.EntityFrameworkCore.DbContext context, Guid tenantId, Guid conversationId, DateTimeOffset at)
    {
        using var scope = db.Api.Services.CreateScope();
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Direction = MessageDirection.In,
            Sender = MessageSender.Customer,
            ContentEnc = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>().Encrypt("cho em hỏi"),
            CreatedAt = at,
        };
        context.Add(message);
        return message.Id;
    }

    private async Task RunAsync(Guid tenantId, Guid messageId)
    {
        using var scope = db.Api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ProcessIncomingMessageJob>().RunAsync(tenantId, messageId, CancellationToken.None);
    }

    private async Task<int> CountAsync(Guid tenantId, Guid conversationId, MessageSender sender)
    {
        await using var context = db.CreateDbContext(tenantId);
        return await context.Messages.CountAsync(m => m.ConversationId == conversationId && m.Sender == sender);
    }

    private async Task SetQuotaAsync(Guid tenantId, int quota)
    {
        await using var context = db.CreateDbContext(tenantId);
        (await context.Tenants.SingleAsync()).ConversationQuota = quota;
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task New_customer_after_quota_goes_to_staff_while_earlier_customers_keep_the_bot()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Hạn mức 2");
        await SetQuotaAsync(tenantId, 2);
        var now = DateTimeOffset.UtcNow;
        var first = await CustomerAsync(tenantId, now.AddMinutes(-3));
        var second = await CustomerAsync(tenantId, now.AddMinutes(-2));
        var third = await CustomerAsync(tenantId, now.AddMinutes(-1));
        await RunAsync(tenantId, first.MessageId);
        await RunAsync(tenantId, second.MessageId);
        await RunAsync(tenantId, third.MessageId);
        await RunAsync(tenantId, third.MessageId); // chạy lại: không gửi trùng câu chuyển tiếp

        (await CountAsync(tenantId, first.ConversationId, MessageSender.Bot)).ShouldBeGreaterThan(0);
        (await CountAsync(tenantId, second.ConversationId, MessageSender.Bot)).ShouldBeGreaterThan(0);
        (await CountAsync(tenantId, third.ConversationId, MessageSender.Bot)).ShouldBe(0);
        (await CountAsync(tenantId, third.ConversationId, MessageSender.System)).ShouldBe(1);
        await using (var context = db.CreateDbContext(tenantId))
        {
            var over = await context.Conversations.SingleAsync(c => c.Id == third.ConversationId);
            over.Mode.ShouldBe(ConversationMode.Human);
            over.HandoffReason.ShouldBe("quota_exceeded");
            over.NeedsAttentionSince.ShouldNotBeNull();
            (await context.UsageRecords.CountAsync(u => u.ConversationId == third.ConversationId)).ShouldBe(0); // không gọi AI

            // Khách đầu tiên quay lại trong tháng (nhân viên đã trả lại bot) → vẫn được bot trả lời dù đã hết hạn mức.
            (await context.Conversations.SingleAsync(c => c.Id == first.ConversationId)).Mode = ConversationMode.Bot;
            await context.SaveChangesAsync();
        }

        var botBefore = await CountAsync(tenantId, first.ConversationId, MessageSender.Bot);
        Guid again;
        await using (var context = db.CreateDbContext(tenantId))
        {
            again = AddCustomerMessage(context, tenantId, first.ConversationId, now);
            await context.SaveChangesAsync();
        }

        await RunAsync(tenantId, again);
        (await CountAsync(tenantId, first.ConversationId, MessageSender.Bot)).ShouldBeGreaterThan(botBefore);

        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var usage = (await owner.GetFromJsonAsync<UsageResponse>(Url("/tenant/usage")))!;
        usage.ShouldSatisfyAllConditions(u => u.Used.ShouldBe(3), u => u.Quota.ShouldBe(2), u => u.Exceeded.ShouldBeTrue(), u => u.Warning.ShouldBeTrue());

        // Đã đặt dấu "đã báo" cho tháng này → không báo lại.
        using var scope = db.Api.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDistributedStore>();
        var month = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyyMM", CultureInfo.InvariantCulture);
        (await store.SetIfNotExistsAsync($"quota-notify:{tenantId:N}:{month}:exceeded", "1", TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Chat_test_is_never_blocked_by_quota()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Hạn mức 0");
        await SetQuotaAsync(tenantId, 0);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var create = await owner.PostAsync(Url("/chat-test/conversations"), null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;
        await owner.PostAsJsonAsync(Url($"/chat-test/conversations/{conversation.Id}/messages"), new { text = "giá?" });
        Guid messageId;
        await using (var context = db.CreateDbContext(tenantId))
        {
            messageId = await context.Messages.Where(m => m.ConversationId == conversation.Id).Select(m => m.Id).SingleAsync();
        }

        await RunAsync(tenantId, messageId);
        (await CountAsync(tenantId, conversation.Id, MessageSender.Bot)).ShouldBeGreaterThan(0);
    }
}
