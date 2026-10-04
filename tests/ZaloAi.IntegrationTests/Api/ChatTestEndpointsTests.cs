using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Knowledge;
using ZaloAi.Api.Tenants;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Chat thử end-to-end với AI giả (Ai:ChatProvider = fake) và embedding giả.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ChatTestEndpointsTests(PostgresFixture db)
{
    private const string Data = """
        {
          "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "price": 450000 } ]
        }
        """;

    private static readonly Uri Conversations = new("/chat-test/conversations", UriKind.Relative);

    private async Task RunJobAsync<TJob>(Func<TJob, Task> run)
        where TJob : notnull
    {
        using var scope = db.Api.Services.CreateScope();
        await run(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    private async Task<(Guid TenantId, HttpClient Client)> TenantWithDataAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var client = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Data)), "file", "data.json" } };
        using var upload = await client.PostAsync(new Uri("/knowledge/imports", UriKind.Relative), form);
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        using var apply = await client.PostAsJsonAsync(
            new Uri($"/knowledge/imports/{preview.Id}/apply", UriKind.Relative),
            new { selections = preview.Items.Select(i => new { key = i.Key }) });
        apply.EnsureSuccessStatusCode();
        await RunJobAsync<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        return (tenantId, client);
    }

    private async Task<ChatTestConversationResponse> SendAndProcessAsync(HttpClient client, Guid tenantId, Guid conversationId, string text)
    {
        using var send = await client.PostAsJsonAsync(new Uri($"/chat-test/conversations/{conversationId}/messages", UriKind.Relative), new { text });
        send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var message = (await send.Content.ReadFromJsonAsync<ChatMessageResponse>())!;

        var pending = (await client.GetFromJsonAsync<ChatTestConversationResponse>(new Uri($"/chat-test/conversations/{conversationId}", UriKind.Relative)))!;
        pending.WaitingForBot.ShouldBeTrue();

        await RunJobAsync<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, message.Id, CancellationToken.None));
        return (await client.GetFromJsonAsync<ChatTestConversationResponse>(new Uri($"/chat-test/conversations/{conversationId}", UriKind.Relative)))!;
    }

    [Fact]
    public async Task Customer_message_gets_bot_reply_with_disclosure_trace_and_usage()
    {
        var (tenantId, client) = await TenantWithDataAsync("Chat thử 1");
        using var create = await client.PostAsync(Conversations, null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;

        var after = await SendAndProcessAsync(client, tenantId, conversation.Id, "Massage đông y 60 phút giá bao nhiêu? SĐT mình 0912345678");

        after.WaitingForBot.ShouldBeFalse();
        after.Messages.Count.ShouldBe(2);
        var reply = after.Messages[1];
        reply.Sender.ShouldBe("bot");
        reply.Text.ShouldContain("trợ lý AI");
        reply.Text.ShouldContain("450");
        reply.Trace.ShouldNotBeNull();
        reply.Trace.Chunks.ShouldContain(c => c.Code == "DV-MASSAGE-60" && c.Used);
        reply.Trace.PiiMasked.ShouldBe(1);
        after.AiCalls.ShouldBe(1);

        // Nội dung tin lưu dạng mã hóa, trace không chứa SĐT.
        using var scope = db.Api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raw = await context.Messages.IgnoreQueryFilters().Where(m => m.ConversationId == conversation.Id).ToListAsync();
        raw.ShouldAllBe(m => !m.ContentEnc.Contains("0912345678") && (m.AiTraceJson == null || !m.AiTraceJson.Contains("0912345678")));

        // Chạy lại job cho cùng tin → không trả lời lần 2.
        await RunJobAsync<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, after.Messages[0].Id, CancellationToken.None));
        var again = (await client.GetFromJsonAsync<ChatTestConversationResponse>(new Uri($"/chat-test/conversations/{conversation.Id}", UriKind.Relative)))!;
        again.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Danger_message_hands_off_and_bot_stops_until_returned()
    {
        var (tenantId, client) = await TenantWithDataAsync("Chat thử khẩn");
        using var create = await client.PostAsync(Conversations, null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;

        var after = await SendAndProcessAsync(client, tenantId, conversation.Id, "massage xong em bị khó thở");
        after.Mode.ShouldBe("human");
        after.Urgency.ShouldBe("urgent");
        after.Messages[1].Text.ShouldContain("115");

        // Đang do nhân viên xử lý: khách vẫn nhắn được, bot không trả lời (job bỏ qua).
        using var whileHuman = await client.PostAsJsonAsync(new Uri($"/chat-test/conversations/{conversation.Id}/messages", UriKind.Relative), new { text = "alo" });
        whileHuman.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var back = await client.PostAsync(new Uri($"/chat-test/conversations/{conversation.Id}/return-to-bot", UriKind.Relative), null);
        (await back.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!.Mode.ShouldBe("bot");
    }

    [Fact]
    public async Task Other_tenant_cannot_see_or_post_to_conversation()
    {
        var (_, clientA) = await TenantWithDataAsync("Chat A");
        var (tenantB, _) = await db.CreateTenantAsync("Chat B");
        var clientB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        using var create = await clientA.PostAsync(Conversations, null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;

        using var get = await clientB.GetAsync(new Uri($"/chat-test/conversations/{conversation.Id}", UriKind.Relative));
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var post = await clientB.PostAsJsonAsync(new Uri($"/chat-test/conversations/{conversation.Id}/messages", UriKind.Relative), new { text = "hi" });
        post.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.GetFromJsonAsync<List<ChatTestConversationSummary>>(Conversations))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Owner_sets_bot_style_and_medical_review()
    {
        var (_, client) = await TenantWithDataAsync("Giọng văn");

        using var style = await client.PutAsJsonAsync(new Uri("/tenant/bot-style", UriKind.Relative), new { botTone = "concise", botInstructions = "Luôn mời khách đặt lịch." });
        var settings = (await style.Content.ReadFromJsonAsync<TenantSettingsResponse>())!;
        settings.BotTone.ShouldBe("concise");
        settings.BotInstructions.ShouldBe("Luôn mời khách đặt lịch.");

        using var bad = await client.PutAsJsonAsync(new Uri("/tenant/bot-style", UriKind.Relative), new { botTone = "1", botInstructions = (string?)null });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var items = (await client.GetFromJsonAsync<List<KnowledgeItemResponse>>(new Uri("/knowledge/items", UriKind.Relative)))!;
        items[0].MedicallyReviewed.ShouldBeFalse();
        using var review = await client.PutAsJsonAsync(new Uri($"/knowledge/items/{items[0].Id}/medically-reviewed", UriKind.Relative), new { reviewed = true });
        (await review.Content.ReadFromJsonAsync<KnowledgeItemResponse>())!.MedicallyReviewed.ShouldBeTrue();
    }
}
