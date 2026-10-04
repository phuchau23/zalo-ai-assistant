using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Connections;
using ZaloAi.Channels.Zalo;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Webhook Zalo → lưu tin → job trả lời → gửi qua Zalo (giả), kèm các tình huống lỗi.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ZaloWebhookTests(PostgresFixture db) : IAsyncLifetime, IDisposable
{
    private readonly FakeZaloServer _zalo = new();
    private ZaloApiFactory _api = null!;

    public Task InitializeAsync()
    {
        _api = new ZaloApiFactory(db.ConnectionString, db.RedisConnectionString, _zalo);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    public void Dispose() => _zalo.Dispose();

    private static string NewId() => Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Tenant mới + OA đã kết nối qua đúng luồng OAuth.</summary>
    private async Task<(Guid TenantId, string OaId, HttpClient Owner)> ConnectedTenantAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var start = await owner.PostAsync(new Uri("/channels/zalo/connect", UriKind.Relative), null);
        var url = (await start.Content.ReadFromJsonAsync<ConnectUrlResponse>())!.Url;
        var state = Uri.UnescapeDataString(url.Split("state=")[1]);
        var oaId = NewId();
        var anonymous = _api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var callback = await anonymous.GetAsync(new Uri($"/connect/zalo/callback?code=c&oa_id={oaId}&state={state}", UriKind.Relative));
        callback.Headers.Location!.Query.ShouldContain("connected=zalo");
        return (tenantId, oaId, owner);
    }

    private static string UserText(string oaId, string userId, string msgId, string text) => new JsonObject
    {
        ["app_id"] = ZaloApiFactory.AppId,
        ["sender"] = new JsonObject { ["id"] = userId },
        ["recipient"] = new JsonObject { ["id"] = oaId },
        ["event_name"] = "user_send_text",
        ["message"] = new JsonObject { ["text"] = text, ["msg_id"] = msgId },
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
    }.ToJsonString();

    private async Task<HttpResponseMessage> PostAsync(string body, string? signature = null)
    {
        var timestamp = JsonDocument.Parse(body).RootElement.GetProperty("timestamp").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/webhooks/zalo", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-ZEvent-Signature", signature ?? "mac=" + ZaloWebhookSignature.Compute(ZaloApiFactory.AppId, body, timestamp, ZaloApiFactory.WebhookSecret));
        return await _api.CreateClient().SendAsync(request);
    }

    private async Task RunJobsAsync(Guid tenantId)
    {
        await using var context = db.CreateDbContext(tenantId);
        var pending = await context.Messages
            .Where(m => m.Sender == MessageSender.Customer)
            .Where(m => !context.Messages.Any(r => r.ReplyToMessageId == m.Id && r.DeliveryStatus != DeliveryStatus.Pending))
            .Select(m => m.Id)
            .ToListAsync();
        foreach (var id in pending)
        {
            using var scope = _api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProcessIncomingMessageJob>().RunAsync(tenantId, id, CancellationToken.None);
        }
    }

    private async Task<List<Message>> MessagesAsync(Guid tenantId)
    {
        await using var context = db.CreateDbContext(tenantId);
        return await context.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync();
    }

    private List<(string Url, string Body, Dictionary<string, string> Headers)> Sends() =>
        _zalo.Calls.Where(c => c.Url.EndsWith("/oa/message/cs", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Customer_message_is_answered_through_zalo()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Webhook trả lời");
        var userId = NewId();

        using var response = await PostAsync(UserText(oaId, userId, "msg-1", "xin chào, sđt mình 0912345678"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId);

        // AI giả không có dữ liệu → trả lời + chuyển nhân viên kèm câu chuyển tiếp (M5).
        var messages = await MessagesAsync(tenantId);
        messages.Select(m => m.Sender).ShouldBe([MessageSender.Customer, MessageSender.Bot, MessageSender.System]);
        messages[0].ExternalMessageId.ShouldBe("msg-1");
        messages[0].ContentEnc.ShouldNotContain("0912345678");
        var reply = messages[1];
        reply.Sender.ShouldBe(MessageSender.Bot);
        reply.DeliveryStatus.ShouldBe(DeliveryStatus.Sent);
        reply.ExternalMessageId.ShouldBe("zalo-msg-1");

        Sends().Count.ShouldBe(2);
        messages[2].DeliveryStatus.ShouldBe(DeliveryStatus.Sent);
        var send = Sends()[0];
        send.Headers["access_token"].ShouldStartWith("AT-");
        var body = JsonDocument.Parse(send.Body).RootElement;
        body.GetProperty("recipient").GetProperty("user_id").GetString().ShouldBe(userId);
        body.GetProperty("message").GetProperty("text").GetString()!.ShouldContain("trợ lý AI");
    }

    [Fact]
    public async Task Wrong_signature_is_rejected_and_nothing_stored()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Chữ ký sai");

        using var forged = await PostAsync(UserText(oaId, NewId(), "msg-x", "hi"), signature: "mac=" + new string('0', 64));
        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var missing = await PostAsync(UserText(oaId, NewId(), "msg-y", "hi"), signature: "");
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await MessagesAsync(tenantId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_webhook_is_answered_once()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Webhook trùng");
        var body = UserText(oaId, NewId(), "msg-dup", "giá massage");

        (await PostAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId);
        await RunJobsAsync(tenantId);

        (await MessagesAsync(tenantId)).Count.ShouldBe(3); // khách, bot, câu chuyển tiếp — không nhân đôi
        Sends().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Bot_echo_is_ignored_but_staff_reply_in_OA_stops_bot()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Nhân viên trong OA");
        var userId = NewId();
        (await PostAsync(UserText(oaId, userId, "msg-a", "hello"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId);

        JsonObject OaSend(string msgId, string? adminId) => new()
        {
            ["app_id"] = ZaloApiFactory.AppId,
            ["sender"] = adminId is null ? new JsonObject { ["id"] = oaId } : new JsonObject { ["id"] = oaId, ["admin_id"] = adminId },
            ["recipient"] = new JsonObject { ["id"] = userId },
            ["event_name"] = "oa_send_text",
            ["message"] = new JsonObject { ["text"] = "Dạ em là nhân viên", ["msg_id"] = msgId },
            ["timestamp"] = "1700000000000",
        };

        (await PostAsync(OaSend("zalo-msg-1", null).ToJsonString())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MessagesAsync(tenantId)).Count.ShouldBe(3); // tin bot dội về: bỏ qua

        (await PostAsync(OaSend("staff-1", "admin-9").ToJsonString())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var messages = await MessagesAsync(tenantId);
        messages.Last().Sender.ShouldBe(MessageSender.Staff);

        // Khách nhắn tiếp: bot không chen vào vì nhân viên đang xử lý.
        (await PostAsync(UserText(oaId, userId, "msg-b", "cảm ơn"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId);
        Sends().Count.ShouldBe(2); // không thêm tin bot nào
    }

    [Fact]
    public async Task Unknown_or_disconnected_OA_is_ignored()
    {
        (await PostAsync(UserText(NewId(), NewId(), "msg-u", "hi"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (tenantId, oaId, owner) = await ConnectedTenantAsync("Đã ngắt");
        var connections = (await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!;
        (await owner.DeleteAsync(new Uri($"/channels/{connections.Connections.Single().Id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await PostAsync(UserText(oaId, NewId(), "msg-d", "hi"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MessagesAsync(tenantId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Expired_token_is_refreshed_and_message_resent_once()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Token hỏng");
        var calls = 0;
        _zalo.SendResponse = _ => Interlocked.Increment(ref calls) == 1
            ? """{"error":-220,"message":"access_token is expired or removed"}"""
            : "{\"data\":{\"message_id\":\"ok-" + calls.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"},\"error\":0,\"message\":\"Success\"}";

        (await PostAsync(UserText(oaId, NewId(), "msg-t", "hi"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId);

        var sends = Sends();
        sends.Count.ShouldBe(3); // lỗi -220, gửi lại câu trả lời, câu chuyển tiếp
        sends[1].Headers["access_token"].ShouldNotBe(sends[0].Headers["access_token"]);
        _zalo.Calls.Count(c => c.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal)).ShouldBe(1);
        (await MessagesAsync(tenantId)).Where(m => m.Direction == MessageDirection.Out).ShouldAllBe(m => m.DeliveryStatus == DeliveryStatus.Sent);
    }

    [Fact]
    public async Task Customer_outside_7_days_marks_reply_failed_without_retry()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Quá 7 ngày");
        _zalo.SendResponse = _ => """{"error":-230,"message":"User has not interacted with the OA in the past 7 days"}""";

        (await PostAsync(UserText(oaId, NewId(), "msg-7", "hi"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId); // không ném lỗi → Hangfire không thử lại

        var reply = (await MessagesAsync(tenantId)).Single(m => m.Sender == MessageSender.Bot);
        reply.DeliveryStatus.ShouldBe(DeliveryStatus.Failed);
        reply.DeliveryError.ShouldBe("zalo:-230");
    }

    [Fact]
    public async Task Image_message_gets_canned_handoff_reply()
    {
        var (tenantId, oaId, _) = await ConnectedTenantAsync("Khách gửi ảnh");
        var body = new JsonObject
        {
            ["app_id"] = ZaloApiFactory.AppId,
            ["sender"] = new JsonObject { ["id"] = NewId() },
            ["recipient"] = new JsonObject { ["id"] = oaId },
            ["event_name"] = "user_send_image",
            ["message"] = new JsonObject
            {
                ["msg_id"] = "img-1",
                ["attachments"] = new JsonArray(new JsonObject { ["type"] = "image", ["payload"] = new JsonObject { ["url"] = "http://f6.photo.talk.zdn.vn/x.jpg" } }),
            },
            ["timestamp"] = "1700000000001",
        }.ToJsonString();

        (await PostAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunJobsAsync(tenantId);

        using var scope = _api.Services.CreateScope();
        var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
        var messages = await MessagesAsync(tenantId);
        encryptor.Decrypt(messages[0].ContentEnc).ShouldBe(InboundText.ForAttachment("image"));
        JsonDocument.Parse(Sends()[0].Body).RootElement.GetProperty("message").GetProperty("text").GetString()!
            .ShouldContain("chưa xem được hình ảnh");
    }

    [Fact]
    public async Task Token_sweep_refreshes_expiring_connection()
    {
        var (tenantId, _, _) = await ConnectedTenantAsync("Làm mới định kỳ");
        await using (var context = db.CreateDbContext(tenantId))
        {
            var connection = await context.ChannelConnections.SingleAsync();
            connection.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            await context.SaveChangesAsync();
        }

        var before = _zalo.Calls.Count(c => c.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal));
        using (var scope = _api.Services.CreateScope())
        {
            var connectionId = (await db.CreateDbContext(tenantId).ChannelConnections.SingleAsync()).Id;
            await scope.ServiceProvider.GetRequiredService<ZaloTokenRefreshJob>().RunAsync(tenantId, connectionId, CancellationToken.None);
        }

        _zalo.Calls.Count(c => c.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal)).ShouldBe(before + 1);
        await using var check = db.CreateDbContext(tenantId);
        (await check.ChannelConnections.SingleAsync()).AccessTokenExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(24));
    }
}
