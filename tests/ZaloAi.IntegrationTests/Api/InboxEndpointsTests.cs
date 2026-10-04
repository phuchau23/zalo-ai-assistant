using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Inbox;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Hộp thư: bot chuyển người → nhân viên thấy nổi bật → tiếp quản / nhắn / trả lại bot (qua webchat Chat thử, AI giả).</summary>
[Collection(PostgresGroup.Name)]
public sealed class InboxEndpointsTests(PostgresFixture db)
{
    private static readonly Uri Conversations = new("/chat-test/conversations", UriKind.Relative);

    private static Uri Inbox(string path = "") => new($"/inbox/conversations{path}", UriKind.Relative);

    private async Task RunPendingJobsAsync(Guid tenantId)
    {
        await using var context = db.CreateDbContext(tenantId);
        var customer = await context.Messages.Where(m => m.Sender == MessageSender.Customer).Select(m => m.Id).ToListAsync();
        var outgoing = await context.Messages.Where(m => m.Direction == MessageDirection.Out && m.DeliveryStatus == DeliveryStatus.Pending)
            .Select(m => m.Id).ToListAsync();
        foreach (var id in customer)
        {
            using var scope = db.Api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProcessIncomingMessageJob>().RunAsync(tenantId, id, CancellationToken.None);
        }

        foreach (var id in outgoing)
        {
            using var scope = db.Api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SendOutgoingMessageJob>().RunAsync(tenantId, id, CancellationToken.None);
        }
    }

    /// <summary>Hội thoại chat thử mà bot đã chuyển người (AI giả không có dữ liệu → không chắc → chuyển người).</summary>
    private async Task<(Guid TenantId, HttpClient Owner, Guid ConversationId)> HandedOffAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var create = await owner.PostAsync(Conversations, null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;
        (await owner.PostAsJsonAsync(new Uri($"/chat-test/conversations/{conversation.Id}/messages", UriKind.Relative), new { text = "có bán vé máy bay không" }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await RunPendingJobsAsync(tenantId);
        return (tenantId, owner, conversation.Id);
    }

    [Fact]
    public async Task Bot_handoff_shows_as_needing_attention_with_handoff_notice()
    {
        var (_, owner, conversationId) = await HandedOffAsync("Hộp thư chuyển người");

        var attention = (await owner.GetFromJsonAsync<List<InboxConversationItem>>(Inbox("?filter=attention")))!;
        var item = attention.ShouldHaveSingleItem();
        item.Id.ShouldBe(conversationId);
        item.Mode.ShouldBe("human");
        item.NeedsAttentionSince.ShouldNotBeNull();
        item.IsTest.ShouldBeTrue();

        var detail = (await owner.GetFromJsonAsync<InboxConversationDetail>(Inbox($"/{conversationId}")))!;
        detail.Messages.Select(m => m.Sender).ShouldBe(["customer", "bot", "system"]);
        detail.Messages[2].Text.ShouldContain("chuyên viên chăm sóc khách hàng");
        detail.Messages.ShouldAllBe(m => m.Sender == "customer" || m.DeliveryStatus == "sent");
    }

    [Fact]
    public async Task Staff_takes_over_replies_with_signature_then_returns_to_bot()
    {
        var (tenantId, owner, conversationId) = await HandedOffAsync("Tiếp quản");

        (await owner.PostAsync(Inbox($"/{conversationId}/take-over"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync(Inbox($"/{conversationId}/messages"), new { text = "Dạ em hỗ trợ chị ạ" })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await RunPendingJobsAsync(tenantId);

        var detail = (await owner.GetFromJsonAsync<InboxConversationDetail>(Inbox($"/{conversationId}")))!;
        detail.Conversation.NeedsAttentionSince.ShouldBeNull();
        detail.Conversation.AssignedUserId.ShouldNotBeNull();
        var staff = detail.Messages[^1];
        staff.Sender.ShouldBe("staff");
        staff.Text.ShouldEndWith(", CSKH");
        staff.DeliveryStatus.ShouldBe("sent");
        detail.Messages.ShouldContain(m => m.Sender == "system" && m.Text.Contains("sẽ hỗ trợ", StringComparison.Ordinal)); // câu tiếp quản

        // Khách (chat thử) thấy tin nhân viên.
        var chat = (await owner.GetFromJsonAsync<ChatTestConversationResponse>(new Uri($"/chat-test/conversations/{conversationId}", UriKind.Relative)))!;
        chat.Messages.ShouldContain(m => m.Sender == "staff");

        // Khách nhắn tiếp khi nhân viên đang xử lý → nổi bật lại, bot không trả lời.
        await owner.PostAsJsonAsync(new Uri($"/chat-test/conversations/{conversationId}/messages", UriKind.Relative), new { text = "cảm ơn em" });
        await RunPendingJobsAsync(tenantId);
        var again = (await owner.GetFromJsonAsync<InboxConversationDetail>(Inbox($"/{conversationId}")))!;
        again.Conversation.NeedsAttentionSince.ShouldNotBeNull();
        again.Messages[^1].Sender.ShouldBe("customer");

        (await owner.PostAsync(Inbox($"/{conversationId}/return-to-bot"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var back = (await owner.GetFromJsonAsync<InboxConversationDetail>(Inbox($"/{conversationId}")))!;
        back.Conversation.Mode.ShouldBe("bot");
        back.Conversation.NeedsAttentionSince.ShouldBeNull();
    }

    [Fact]
    public async Task Tenant_B_cannot_see_or_act_on_tenant_A_conversations()
    {
        var (_, _, conversationId) = await HandedOffAsync("Hộp thư A");
        var (tenantB, _) = await db.CreateTenantAsync("Hộp thư B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        (await ownerB.GetFromJsonAsync<List<InboxConversationItem>>(Inbox()))!.ShouldBeEmpty();
        (await ownerB.GetAsync(Inbox($"/{conversationId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsync(Inbox($"/{conversationId}/take-over"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Inbox($"/{conversationId}/messages"), new { text = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Staff_can_reply_but_only_owner_assigns_and_edits_settings()
    {
        var (tenantId, owner, conversationId) = await HandedOffAsync("Phân quyền hộp thư");
        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        var staff = await db.Api.CreateLoggedInClientAsync(staffEmail);

        (await staff.PostAsJsonAsync(Inbox($"/{conversationId}/messages"), new { text = "Dạ em đây" })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await staff.PostAsJsonAsync(Inbox($"/{conversationId}/assign"), new { userId = (Guid?)null })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var members = (await owner.GetFromJsonAsync<List<TenantMemberResponse>>(new Uri("/tenant/members", UriKind.Relative)))!;
        var staffMember = members.Single(m => m.Role == "staff");
        (await owner.PostAsJsonAsync(Inbox($"/{conversationId}/assign"), new { userId = staffMember.UserId })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PostAsJsonAsync(Inbox($"/{conversationId}/assign"), new { userId = Guid.NewGuid() })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await staff.GetFromJsonAsync<List<InboxConversationItem>>(Inbox("?filter=mine")))!.ShouldHaveSingleItem();

        var settings = (await staff.GetFromJsonAsync<HandoffSettingsResponse>(new Uri("/tenant/handoff-settings", UriKind.Relative)))!;
        var update = new UpdateHandoffSettingsRequest(
            settings.HandoffMessage, settings.AfterHoursMessage, false, settings.TakeoverMessage, true, settings.ReturnToBotMessage,
            false, "10 phút", "08:00", "21:00", 127, 15, "-1001234567890");
        (await staff.PutAsJsonAsync(new Uri("/tenant/handoff-settings", UriKind.Relative), update)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var saved = await owner.PutAsJsonAsync(new Uri("/tenant/handoff-settings", UriKind.Relative), update);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await saved.Content.ReadFromJsonAsync<HandoffSettingsResponse>())!.ReminderMinutes.ShouldBe(15);

        using var invalid = await owner.PutAsJsonAsync(new Uri("/tenant/handoff-settings", UriKind.Relative), update with { OpenTime = "22:00", TelegramChatId = "abc" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reminder_sweep_reminds_once_per_interval()
    {
        var (tenantId, _, conversationId) = await HandedOffAsync("Nhắc chờ lâu");
        await using (var context = db.CreateDbContext(tenantId))
        {
            var c = await context.Conversations.SingleAsync(x => x.Id == conversationId);
            c.IsTest = false; // ca thật
            c.NeedsAttentionSince = DateTimeOffset.UtcNow.AddMinutes(-11);
            await context.SaveChangesAsync();
        }

        using (var scope = db.Api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AttentionReminderJob>().RunAsync(tenantId, conversationId, CancellationToken.None);
        }

        await using var check = db.CreateDbContext(tenantId);
        (await check.Conversations.SingleAsync(x => x.Id == conversationId)).LastReminderAt.ShouldNotBeNull();
    }
}
