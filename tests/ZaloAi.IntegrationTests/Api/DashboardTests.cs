using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Dashboard;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Dashboard chủ DN (M6 đợt B1): số liệu đúng, bỏ Chat thử, che dữ liệu cá nhân, chỉ owner, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class DashboardTests(PostgresFixture db)
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>2 hội thoại Zalo (1 bot tự xử lý, 1 chuyển người vì thiếu dữ liệu) + 1 hội thoại Chat thử + chi phí AI.</summary>
    private async Task SeedAsync(Guid tenantId)
    {
        using var scope = db.Api.Services.CreateScope();
        var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
        await using var context = db.CreateDbContext(tenantId);

        void Conversation(ChannelKind channel, LeadStatus lead, string question, string trace)
        {
            var contact = new Contact { Id = Guid.CreateVersion7(), TenantId = tenantId, Channel = channel, ExternalUserId = Guid.NewGuid().ToString("N"), LeadStatus = lead };
            var conversation = new Conversation { Id = Guid.CreateVersion7(), TenantId = tenantId, ContactId = contact.Id, Channel = channel, IsTest = channel == ChannelKind.Webchat };
            var now = DateTimeOffset.UtcNow;
            context.Contacts.Add(contact);
            context.Conversations.Add(conversation);
            context.Messages.Add(new Message
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConversationId = conversation.Id,
                Direction = MessageDirection.In,
                Sender = MessageSender.Customer,
                ContentEnc = encryptor.Encrypt(question),
                CreatedAt = now.AddSeconds(-2),
            });
            context.Messages.Add(new Message
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConversationId = conversation.Id,
                Direction = MessageDirection.Out,
                Sender = MessageSender.Bot,
                ContentEnc = encryptor.Encrypt("Dạ ..."),
                AiTraceJson = trace,
                CreatedAt = now.AddSeconds(-1),
            });
        }

        Conversation(ChannelKind.Zalo, LeadStatus.Interested, "giá massage 60 phút?", """{"needsHuman":false,"handoffReason":null}""");
        Conversation(ChannelKind.Zalo, LeadStatus.New, "Có nối mi không? sđt mình 0912345678", """{"needsHuman":true,"handoffReason":"no_knowledge"}""");
        Conversation(ChannelKind.Webchat, LeadStatus.Hot, "chat thử: có nối mi không", """{"needsHuman":true,"handoffReason":"no_knowledge"}""");
        context.UsageRecords.Add(new UsageRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, Kind = UsageKind.Chat, Provider = "fake", Model = "m", CostUsd = 0.0125m });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Owner_sees_real_conversations_bot_rate_and_masked_unanswered_questions()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Dashboard A");
        await SeedAsync(tenantId);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));

        var result = (await owner.GetFromJsonAsync<DashboardResponse>(Url("/dashboard")))!;

        result.Days.ShouldBe(7);
        result.Daily.Count.ShouldBe(7);
        result.Daily[^1].Conversations.ShouldBe(2); // Chat thử không tính
        result.Conversations.ShouldBe(2);
        result.BotHandled.ShouldBe(1);
        result.NewContacts.ShouldBe(2);
        result.NewLeads.ShouldBe(1);
        result.AiCostUsdMonth.ShouldBe(0.0125m);
        var question = result.Unanswered.ShouldHaveSingleItem();
        question.Reason.ShouldBe("no_knowledge");
        question.Question.ShouldNotBeNull();
        question.Question.ShouldContain("nối mi");
        question.Question.ShouldNotContain("0912345678");

        (await owner.GetFromJsonAsync<DashboardResponse>(Url("/dashboard?days=30")))!.Daily.Count.ShouldBe(30);
        (await owner.GetAsync(Url("/dashboard?days=10"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var context = db.CreateDbContext(tenantId);
        context.AuditLogs.Count(a => a.Action == "dashboard.unanswered_viewed").ShouldBe(1); // xem lại trong 30 phút không ghi thêm
    }

    [Fact]
    public async Task Staff_cannot_open_and_tenant_B_sees_none_of_tenant_A_data()
    {
        var (tenantA, _) = await db.CreateTenantAsync("Dashboard cô lập A");
        await SeedAsync(tenantA);
        var staffA = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantA, TenantRole.Staff));
        (await staffA.GetAsync(Url("/dashboard"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (tenantB, _) = await db.CreateTenantAsync("Dashboard cô lập B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        var result = (await ownerB.GetFromJsonAsync<DashboardResponse>(Url("/dashboard")))!;
        result.Conversations.ShouldBe(0);
        result.NewContacts.ShouldBe(0);
        result.AiCostUsdMonth.ShouldBe(0m);
        result.Unanswered.ShouldBeEmpty();
    }
}
