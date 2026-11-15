using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Connections;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>
/// Kênh Zalo cá nhân qua sidecar (giả): đăng nhập QR, sự kiện vào → lưu tin → bot trả lời qua sidecar, lọc chuyện riêng,
/// chủ nick nhắn tay → bot tạm im, mất kết nối, cổng nội bộ + chữ ký, lease, cô lập tenant.
/// </summary>
[Collection(PostgresGroup.Name)]
public sealed class ZaloPersonalTests(PostgresFixture db) : IAsyncLifetime, IDisposable
{
    private const string Instance = "test-sidecar";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly FakeSidecarServer _sidecar = new();
    private ZaloPersonalApiFactory _api = null!;

    public Task InitializeAsync()
    {
        _api = new ZaloPersonalApiFactory(db.ConnectionString, db.RedisConnectionString, _sidecar);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    public void Dispose() => _sidecar.Dispose();

    private static string NewId() => Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static object Session() => new
    {
        cookie = new[] { new { key = "zpw_sek", value = "secret-cookie-value" } },
        imei = "imei-123-secret",
        userAgent = "Mozilla/5.0 test",
    };

    private HttpClient Internal() => _api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:4001") });

    private async Task<HttpResponseMessage> InternalAsync(string path, object body, string? signature = null, HttpClient? client = null)
    {
        var json = JsonSerializer.Serialize(body, Web);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(
            RequestSignature.HeaderName,
            signature ?? RequestSignature.Create(ZaloPersonalApiFactory.SharedSecret, DateTimeOffset.UtcNow, "POST", path, json));
        return await (client ?? Internal()).SendAsync(request);
    }

    private static async Task<string> StartLoginAsync(HttpClient owner)
    {
        using var start = await owner.PostAsync(new Uri("/channels/zalo-personal/login", UriKind.Relative), null);
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await start.Content.ReadFromJsonAsync<PersonalLoginStartResponse>())!.LoginId;
    }

    /// <summary>Tenant mới + nick đã kết nối qua đúng luồng QR.</summary>
    private async Task<(Guid TenantId, HttpClient Owner, Guid ConnectionId, string Uid)> ConnectedTenantAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var loginId = await StartLoginAsync(owner);
        var uid = NewId();
        using var result = await InternalAsync("/internal/zalo-personal/login-result", new { loginId, uid, displayName = "Nick shop", session = Session() });
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var connectionId = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionId").GetGuid();
        return (tenantId, owner, connectionId, uid);
    }

    private static object Message(string fromUid, string msgId, string text, bool isFriend = false, bool isSelf = false, string? threadId = null) => new
    {
        type = "message",
        msgId,
        threadId = threadId ?? fromUid,
        fromUid,
        fromName = "Chị Hoa",
        isSelf,
        isFriend,
        kind = "text",
        text,
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private async Task EventsAsync(Guid connectionId, params object[] events)
    {
        using var response = await InternalAsync("/internal/zalo-personal/events", new { connectionId, events });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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

    private async Task<JsonElement> SyncAsync(string instance = Instance, object[]? running = null)
    {
        using var response = await InternalAsync("/internal/zalo-personal/sync", new { instance, running = running ?? [] });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private List<(string Method, string Path, string Body, string? Signature)> SidecarCalls(string suffix) =>
        _sidecar.Calls.Where(c => c.Path.EndsWith(suffix, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Internal_endpoints_need_the_internal_port_and_a_valid_signature()
    {
        var body = new { instance = Instance, running = Array.Empty<object>() };

        using var publicPort = await InternalAsync("/internal/zalo-personal/sync", body, client: _api.CreateClient());
        publicPort.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var forged = await InternalAsync("/internal/zalo-personal/sync", body, signature: "t=1,v1=" + new string('0', 64));
        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var old = RequestSignature.Create(ZaloPersonalApiFactory.SharedSecret, DateTimeOffset.UtcNow.AddMinutes(-10), "POST", "/internal/zalo-personal/sync",
            JsonSerializer.Serialize(body, Web));
        using var replayed = await InternalAsync("/internal/zalo-personal/sync", body, signature: old);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var ok = await InternalAsync("/internal/zalo-personal/sync", body);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Qr_login_connects_the_nick_and_keeps_the_session_encrypted()
    {
        var (tenantId, _) = await db.CreateTenantAsync("Zalo cá nhân QR");
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var loginId = await StartLoginAsync(owner);

        var login = SidecarCalls("/login-qr").Single(c => c.Body.Contains(loginId, StringComparison.Ordinal));
        RequestSignature.IsValid(login.Signature, ZaloPersonalApiFactory.SharedSecret, DateTimeOffset.UtcNow, "POST", "/login-qr", login.Body).ShouldBeTrue();
        JsonDocument.Parse(login.Body).RootElement.GetProperty("userAgent").GetString().ShouldNotBeNullOrEmpty();

        using (var progress = await InternalAsync("/internal/zalo-personal/login-progress", new { loginId, state = "qr", qrPng = "iVBORw0KGgo=" }))
        {
            progress.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var waiting = await owner.GetFromJsonAsync<PersonalLoginStatusResponse>(new Uri($"/channels/zalo-personal/login/{loginId}", UriKind.Relative));
        waiting!.State.ShouldBe("qr");
        waiting.QrPng.ShouldBe("iVBORw0KGgo=");

        var uid = NewId();
        using (var result = await InternalAsync("/internal/zalo-personal/login-result", new { loginId, uid, displayName = "Spa An Nhiên", session = Session() }))
        {
            result.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var done = await owner.GetFromJsonAsync<PersonalLoginStatusResponse>(new Uri($"/channels/zalo-personal/login/{loginId}", UriKind.Relative));
        done!.State.ShouldBe("success");
        done.ConnectionId.ShouldNotBeNull();

        var channels = await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative));
        var nick = channels!.Connections.Single();
        nick.Channel.ShouldBe("zalopersonal");
        nick.Name.ShouldBe("Spa An Nhiên");
        nick.Status.ShouldBe("active");
        nick.Personal!.ListenerState.ShouldBe("starting");
        nick.AccessTokenExpiresAt.ShouldBeNull();
        (await owner.GetStringAsync(new Uri("/channels", UriKind.Relative))).ShouldNotContain("secret-cookie-value");

        await using (var context = db.CreateDbContext(tenantId))
        {
            var account = await context.ZaloPersonalAccounts.SingleAsync();
            account.SessionEnc.ShouldNotContain("secret-cookie-value");
            account.SessionEnc.ShouldNotContain("imei-123-secret");
        }

        // Sidecar đồng bộ → nhận lại đúng phiên để chạy nick.
        var sync = await SyncAsync();
        sync.GetProperty("leaseGranted").GetBoolean().ShouldBeTrue();
        var target = sync.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("connectionId").GetGuid() == done.ConnectionId);
        target.GetProperty("session").GetProperty("imei").GetString().ShouldBe("imei-123-secret");
    }

    [Fact]
    public async Task A_nick_connected_to_another_business_is_refused()
    {
        var (_, _, _, uid) = await ConnectedTenantAsync("Nick đã thuộc DN A");
        var (otherTenant, _) = await db.CreateTenantAsync("DN B muốn dùng cùng nick");
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(otherTenant));
        var loginId = await StartLoginAsync(owner);

        using var result = await InternalAsync("/internal/zalo-personal/login-result", new { loginId, uid, displayName = "x", session = Session() });

        result.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("already_connected_elsewhere");
        await using var context = db.CreateDbContext(otherTenant);
        (await context.ChannelConnections.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Stranger_message_is_answered_through_the_sidecar_once()
    {
        var (tenantId, _, connectionId, _) = await ConnectedTenantAsync("Khách lạ nhắn nick");
        var customer = NewId();

        await EventsAsync(connectionId, Message(customer, "zp-in-1", "chào shop, sđt mình 0912345678"));
        await EventsAsync(connectionId, Message(customer, "zp-in-1", "chào shop, sđt mình 0912345678")); // sidecar gửi lại
        await RunJobsAsync(tenantId);

        var messages = await MessagesAsync(tenantId);
        messages.Count(m => m.Sender == MessageSender.Customer).ShouldBe(1);
        messages[0].ContentEnc.ShouldNotContain("0912345678");
        var reply = messages.First(m => m.Sender == MessageSender.Bot);
        reply.DeliveryStatus.ShouldBe(DeliveryStatus.Sent);
        reply.ExternalMessageId.ShouldStartWith("zp-msg-");

        var send = SidecarCalls("/send").First(c => c.Path == $"/accounts/{connectionId}/send");
        var body = JsonDocument.Parse(send.Body).RootElement;
        body.GetProperty("threadId").GetString().ShouldBe(customer);
        body.GetProperty("messageId").GetGuid().ShouldBe(reply.Id);
        body.GetProperty("typingMs").GetInt32().ShouldBeGreaterThan(0);

        await using var context = db.CreateDbContext(tenantId);
        var contact = await context.Contacts.SingleAsync();
        contact.Channel.ShouldBe(ChannelKind.ZaloPersonal);
        contact.ExternalUserId.ShouldBe(customer);
        (await context.ZaloPeers.SingleAsync()).Kind.ShouldBe(PeerKind.Customer);
    }

    [Fact]
    public async Task Customer_writing_again_while_the_bot_types_drops_the_stale_reply()
    {
        // Lỗi thật 2026-11-14: bot trả lời 2 đoạn, khách nhắn "có" giữa 2 đoạn → đoạn 2 ("nếu chưa cần thì thong thả") hiện sau "có".
        var (tenantId, _, connectionId, _) = await ConnectedTenantAsync("Khách nhắn chen");
        var customer = NewId();
        await EventsAsync(connectionId, Message(customer, "zp-chen-1", "bên em làm gì vậy"));
        // Sidecar thấy khách vừa nhắn tin mới hơn tin bot đang trả lời → bỏ. Tin không mang replyTo (câu hệ thống) vẫn gửi.
        _sidecar.SendResponse = body => body.Contains("\"replyTo\":\"zp-chen-1\"", StringComparison.Ordinal)
            ? (HttpStatusCode.Conflict, """{"error":"superseded"}""")
            : (HttpStatusCode.OK, $$"""{"msgId":"zp-msg-{{Guid.NewGuid():N}}"}""");

        await RunJobsAsync(tenantId);

        var stale = (await MessagesAsync(tenantId)).Where(m => m.Sender == MessageSender.Bot).ToList();
        stale.ShouldNotBeEmpty();
        stale.ShouldAllBe(m => m.DeliveryStatus == DeliveryStatus.Skipped && m.DeliveryError == "superseded");
        SidecarCalls("/send").Count(c => c.Body.Contains("\"replyTo\":\"zp-chen-1\"", StringComparison.Ordinal))
            .ShouldBe(1); // đoạn đầu bị bỏ → các đoạn sau không gửi nữa

        // Tin mới của khách được trả lời bình thường; đoạn cũ không tới khách nên câu báo trợ lý AI vẫn có ở lần trả lời này.
        // (Không có dữ liệu → bot vừa chuyển nhân viên; trả lại bot để thử tiếp.)
        await using (var context = db.CreateDbContext(tenantId))
        {
            await context.Conversations.ExecuteUpdateAsync(c => c.SetProperty(x => x.Mode, ConversationMode.Bot));
        }

        _sidecar.SendResponse = _ => (HttpStatusCode.OK, $$"""{"msgId":"zp-msg-{{Guid.NewGuid():N}}"}""");
        await EventsAsync(connectionId, Message(customer, "zp-chen-2", "có"));
        await RunJobsAsync(tenantId);

        var replies = (await MessagesAsync(tenantId)).Where(m => m.Sender == MessageSender.Bot && m.DeliveryStatus == DeliveryStatus.Sent).ToList();
        replies.ShouldNotBeEmpty();
        SidecarCalls("/send").ShouldContain(c => c.Body.Contains("\"replyTo\":\"zp-chen-2\"", StringComparison.Ordinal));
        var encryptor = _api.Services.GetRequiredService<ZaloAi.Core.Security.IFieldEncryptor>();
        encryptor.Decrypt(replies[0].ContentEnc).ShouldContain("trợ lý AI");
    }

    [Fact]
    public async Task Friends_stay_private_until_the_owner_marks_them_as_customers()
    {
        var (tenantId, owner, connectionId, _) = await ConnectedTenantAsync("Bạn bè nhắn nick");
        var friend = NewId();

        await EventsAsync(connectionId, Message(friend, "zp-f-1", "tối nay ăn gì", isFriend: true));
        (await MessagesAsync(tenantId)).ShouldBeEmpty();

        var pending = await owner.GetFromJsonAsync<List<ZaloPeerResponse>>(new Uri($"/channels/zalo-personal/{connectionId}/peers?kind=pending", UriKind.Relative));
        var peer = pending!.Single();
        peer.ExternalUserId.ShouldBe(friend);

        using (var mark = await owner.PutAsJsonAsync(new Uri($"/channels/zalo-personal/peers/{peer.Id}", UriKind.Relative), new { kind = "customer" }))
        {
            mark.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EventsAsync(connectionId, Message(friend, "zp-f-2", "cho mình hỏi giá liệu trình", isFriend: true));
        (await MessagesAsync(tenantId)).Count(m => m.Sender == MessageSender.Customer).ShouldBe(1);

        // Đánh dấu riêng tư → bỏ qua hẳn.
        var other = NewId();
        await EventsAsync(connectionId, Message(other, "zp-p-1", "chuyện riêng", isFriend: true));
        var otherPeer = (await owner.GetFromJsonAsync<List<ZaloPeerResponse>>(new Uri($"/channels/zalo-personal/{connectionId}/peers?kind=pending", UriKind.Relative)))!.Single();
        using (var mark = await owner.PutAsJsonAsync(new Uri($"/channels/zalo-personal/peers/{otherPeer.Id}", UriKind.Relative), new { kind = "private" }))
        {
            mark.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await EventsAsync(connectionId, Message(other, "zp-p-2", "vẫn chuyện riêng", isFriend: true));
        (await MessagesAsync(tenantId)).Count(m => m.Sender == MessageSender.Customer).ShouldBe(1);
    }

    [Fact]
    public async Task Owner_typing_on_the_phone_pauses_the_bot_until_resumed()
    {
        var (tenantId, owner, connectionId, _) = await ConnectedTenantAsync("Chủ nick nhắn tay");
        var customer = NewId();

        // Khách nhắn; trong lúc job còn chờ (gom tin), chủ nick trả lời tay trên điện thoại → job chạy thấy bot đang tạm im.
        await EventsAsync(connectionId, Message(customer, "zp-1", "xin chào"));
        await EventsAsync(connectionId, Message(NewId(), "zp-self-1", "để chị tư vấn trực tiếp nhé", isSelf: true, threadId: customer));
        await RunJobsAsync(tenantId);

        var messages = await MessagesAsync(tenantId);
        messages.Count(m => m.Sender == MessageSender.Staff).ShouldBe(1);
        messages.ShouldNotContain(m => m.Sender == MessageSender.Bot); // bot im
        SidecarCalls("/send").ShouldBeEmpty();
        Conversation conversation;
        await using (var context = db.CreateDbContext(tenantId))
        {
            conversation = await context.Conversations.SingleAsync();
            conversation.BotPausedUntil.ShouldNotBeNull();
            conversation.Mode.ShouldBe(ConversationMode.Bot);
        }

        using var resume = await owner.PostAsync(new Uri($"/inbox/conversations/{conversation.Id}/resume-bot", UriKind.Relative), null);
        resume.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.Conversations.SingleAsync()).BotPausedUntil.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Opening_zalo_web_elsewhere_disconnects_until_the_owner_reconnects()
    {
        var (tenantId, owner, connectionId, _) = await ConnectedTenantAsync("Mở Zalo Web nơi khác");

        await EventsAsync(connectionId, new { type = "state", state = "opened_elsewhere", code = 3000, ts = 1 });

        await using (var context = db.CreateDbContext(tenantId))
        {
            var connection = await context.ChannelConnections.SingleAsync();
            connection.Status.ShouldBe(ConnectionStatus.Disconnected);
            connection.LastError.ShouldBe("zp:opened_elsewhere");
            (await context.ZaloPersonalAccounts.SingleAsync()).LastStateCode.ShouldBe(3000);
        }

        (await SyncAsync()).GetProperty("accounts").EnumerateArray()
            .Any(a => a.GetProperty("connectionId").GetGuid() == connectionId).ShouldBeFalse();

        using var reconnect = await owner.PostAsync(new Uri($"/channels/zalo-personal/{connectionId}/reconnect", UriKind.Relative), null);
        reconnect.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SidecarCalls("/start").ShouldContain(c => c.Path == $"/accounts/{connectionId}/start");
        (await SyncAsync()).GetProperty("accounts").EnumerateArray()
            .Any(a => a.GetProperty("connectionId").GetGuid() == connectionId).ShouldBeTrue();
    }

    [Fact]
    public async Task Expired_session_needs_a_new_qr_scan()
    {
        var (_, owner, connectionId, _) = await ConnectedTenantAsync("Phiên hết hạn");

        await EventsAsync(connectionId, new { type = "state", state = "session_expired", ts = 1 });

        using var reconnect = await owner.PostAsync(new Uri($"/channels/zalo-personal/{connectionId}/reconnect", UriKind.Relative), null);
        await reconnect.ShouldBeProblemAsync(StatusCodes.Status409Conflict, "zalo_personal_needs_qr");
        var channels = await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative));
        channels!.Connections.Single().Status.ShouldBe("needsreauth");
    }

    [Fact]
    public async Task Only_the_sidecar_holding_the_lease_gets_the_nicks()
    {
        await ConnectedTenantAsync("Lease sidecar");

        (await SyncAsync()).GetProperty("leaseGranted").GetBoolean().ShouldBeTrue();
        var intruder = await SyncAsync(instance: "second-sidecar");
        intruder.GetProperty("leaseGranted").GetBoolean().ShouldBeFalse();
        intruder.GetProperty("accounts").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Disconnect_wipes_the_session_and_stops_the_nick()
    {
        var (tenantId, owner, connectionId, _) = await ConnectedTenantAsync("Ngắt nick");

        using var delete = await owner.DeleteAsync(new Uri($"/channels/{connectionId}", UriKind.Relative));
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.ZaloPersonalAccounts.CountAsync()).ShouldBe(0);
        }

        SidecarCalls("/stop").ShouldContain(c => c.Path == $"/accounts/{connectionId}/stop");
        using var late = await InternalAsync("/internal/zalo-personal/events", new { connectionId, events = new[] { Message(NewId(), "zp-late", "hi") } });
        late.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_business_cannot_see_or_touch_the_nick()
    {
        var (_, ownerA, connectionId, _) = await ConnectedTenantAsync("DN A có nick");
        await EventsAsync(connectionId, Message(NewId(), "zp-iso-1", "bạn A", isFriend: true));
        var peerId = (await ownerA.GetFromJsonAsync<List<ZaloPeerResponse>>(new Uri($"/channels/zalo-personal/{connectionId}/peers", UriKind.Relative)))!.Single().Id;
        var loginA = await StartLoginAsync(ownerA);

        var (tenantB, _) = await db.CreateTenantAsync("DN B tò mò");
        var ownerB = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        (await ownerB.GetAsync(new Uri($"/channels/zalo-personal/{connectionId}/peers", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PutAsJsonAsync(new Uri($"/channels/zalo-personal/peers/{peerId}", UriKind.Relative), new { kind = "customer" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsync(new Uri($"/channels/zalo-personal/{connectionId}/reconnect", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PutAsJsonAsync(new Uri($"/channels/zalo-personal/{connectionId}/settings", UriKind.Relative), new { treatFriendsAsCustomers = true, staffPauseMinutes = 5 }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetAsync(new Uri($"/channels/zalo-personal/login/{loginA}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.DeleteAsync(new Uri($"/channels/{connectionId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!.Connections.ShouldBeEmpty();

        var peers = await ownerA.GetFromJsonAsync<List<ZaloPeerResponse>>(new Uri($"/channels/zalo-personal/{connectionId}/peers", UriKind.Relative));
        peers!.Single().Kind.ShouldBe("pending"); // B không đổi được
    }

    [Fact]
    public async Task Staff_cannot_manage_the_owners_personal_nick()
    {
        var (tenantId, _, connectionId, _) = await ConnectedTenantAsync("Nhân viên không quản nick");
        var staff = await _api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, ZaloAi.Core.Tenancy.TenantRole.Staff));

        (await staff.GetAsync(new Uri($"/channels/zalo-personal/{connectionId}/peers", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.PostAsync(new Uri("/channels/zalo-personal/login", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
