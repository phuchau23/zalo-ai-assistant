using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Admin;
using ZaloAi.Api.Connections;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.Infrastructure.ZaloPersonal;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Z2: kho proxy (super admin), proxy đi theo nick, proxy hỏng → nick dừng (fail-closed), nhịp gửi, chặn khi vượt giới hạn.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ZaloPersonalPacingTests(PostgresFixture db) : IAsyncLifetime, IDisposable
{
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

    private static object Session() => new { cookie = new[] { new { key = "k", value = "v" } }, imei = "imei", userAgent = "UA-OLD" };

    private async Task<HttpResponseMessage> InternalAsync(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, Web);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(RequestSignature.HeaderName, RequestSignature.Create(ZaloPersonalApiFactory.SharedSecret, DateTimeOffset.UtcNow, "POST", path, json));
        return await _api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:4001") }).SendAsync(request);
    }

    private async Task<Guid> ConnectAsync(HttpClient owner, Guid? existing = null)
    {
        using var start = await owner.PostAsJsonAsync(new Uri("/channels/zalo-personal/login", UriKind.Relative), new { connectionId = existing });
        start.StatusCode.ShouldBe(HttpStatusCode.OK);
        var loginId = (await start.Content.ReadFromJsonAsync<PersonalLoginStartResponse>())!.LoginId;
        using var result = await InternalAsync("/internal/zalo-personal/login-result", new { loginId, uid = NewId(), displayName = "Nick", session = Session() });
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionId").GetGuid();
    }

    private async Task<(Guid TenantId, HttpClient Owner)> TenantAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        return (tenantId, await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId)));
    }

    private static async Task<AdminProxyResponse> AddProxyAsync(HttpClient admin, string url)
    {
        using var add = await admin.PostAsJsonAsync(new Uri("/admin/proxies", UriKind.Relative), new { label = "VN test", url, country = "VN" });
        add.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await add.Content.ReadFromJsonAsync<AdminProxyResponse>())!;
    }

    private async Task<JsonElement> SyncAsync()
    {
        using var response = await InternalAsync("/internal/zalo-personal/sync", new { instance = "test-sidecar", running = Array.Empty<object>() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task RunProxyHealthAsync()
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ProxyHealthJob>().RunAsync(CancellationToken.None);
    }

    /// <summary>Một khách nhắn qua lại trên một kênh (null = kênh khác) với các tin ở các thời điểm cho trước.</summary>
    private async Task SeedChatAsync(Guid tenantId, Guid? connectionId, params DateTimeOffset[] at)
    {
        using var scope = _api.Services.CreateScope();
        var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
        await using var context = db.CreateDbContext(tenantId);
        var contact = new Contact { Id = Guid.CreateVersion7(), TenantId = tenantId, Channel = ChannelKind.ZaloPersonal, ExternalUserId = NewId() };
        var conversation = new Conversation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ContactId = contact.Id,
            Channel = ChannelKind.ZaloPersonal,
            ConnectionId = connectionId,
        };
        context.Contacts.Add(contact);
        context.Conversations.Add(conversation);
        foreach (var t in at)
        {
            context.Messages.Add(new Message
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ConversationId = conversation.Id,
                Direction = MessageDirection.In,
                Sender = MessageSender.Customer,
                ContentEnc = encryptor.Encrypt("chào"),
                CreatedAt = t.ToUniversalTime(),
            });
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Nick_card_counts_customers_per_day_for_that_nick_only()
    {
        var (tenantA, ownerA) = await TenantAsync("Đếm khách A");
        var (tenantB, ownerB) = await TenantAsync("Đếm khách B");
        var nickA = await ConnectAsync(ownerA);
        var nickB = await ConnectAsync(ownerB);

        // Giữa trưa giờ Việt Nam để không rơi vào ranh giới ngày.
        var vn = TimeSpan.FromHours(7);
        var nowVn = DateTimeOffset.UtcNow.ToOffset(vn);
        var todayNoon = new DateTimeOffset(nowVn.Year, nowVn.Month, nowVn.Day, 12, 0, 0, vn);
        await SeedChatAsync(tenantA, nickA, todayNoon, todayNoon.AddMinutes(5)); // 2 tin, vẫn là 1 khách
        await SeedChatAsync(tenantA, nickA, todayNoon);
        await SeedChatAsync(tenantA, nickA, todayNoon.AddDays(-1));
        await SeedChatAsync(tenantA, nickA, todayNoon.AddDays(-8)); // ngoài 7 ngày
        await SeedChatAsync(tenantA, null, todayNoon);              // kênh khác của A
        await SeedChatAsync(tenantB, nickB, todayNoon);

        var daily = (await ownerA.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!
            .Connections.Single(c => c.Id == nickA).CustomersDaily!;
        daily.ShouldBe([0, 0, 0, 0, 0, 1, 2]);

        var dailyB = (await ownerB.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!
            .Connections.ShouldHaveSingleItem().CustomersDaily!;
        dailyB.ShouldBe([0, 0, 0, 0, 0, 0, 1]);
    }

    [Fact]
    public async Task Only_super_admin_manages_proxies_and_passwords_never_come_back()
    {
        var admin = await _api.CreateLoggedInClientAsync(await db.CreateSuperAdminAsync());
        var url = $"http://user:pw-{NewId()}@proxy-{NewId()}.vn:8080";
        var proxy = await AddProxyAsync(admin, url);
        proxy.Endpoint.ShouldEndWith(":8080");

        using (var duplicate = await admin.PostAsJsonAsync(new Uri("/admin/proxies", UriKind.Relative), new { label = "x", url }))
        {
            await duplicate.ShouldBeProblemAsync(StatusCodes.Status409Conflict, "proxy_duplicate");
        }

        using (var invalid = await admin.PostAsJsonAsync(new Uri("/admin/proxies", UriKind.Relative), new { label = "x", url = "socks5://a:b@c:1" }))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await admin.GetStringAsync(new Uri("/admin/proxies", UriKind.Relative))).ShouldNotContain("pw-");

        var (_, owner) = await TenantAsync("DN không được xem proxy");
        (await owner.GetAsync(new Uri("/admin/proxies", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync(new Uri("/admin/proxies", UriKind.Relative), new { label = "x", url = "http://a:b@c.vn:1" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task New_nick_logs_in_and_runs_through_its_own_proxy_and_rescan_keeps_device_and_proxy()
    {
        var admin = await _api.CreateLoggedInClientAsync(await db.CreateSuperAdminAsync());
        var url = $"http://u:p@own-{NewId()}.vn:9000";
        await AddProxyAsync(admin, url);
        var (tenantId, owner) = await TenantAsync("Nick có IP riêng");

        var connectionId = await ConnectAsync(owner);

        // Proxy được cấp trước khi hiện QR → QR quét qua đúng proxy (có thể là proxy trống khác trong kho, nhưng phải có).
        var login = _sidecar.Calls.Last(c => c.Path == "/login-qr");
        var proxyUrl = JsonDocument.Parse(login.Body).RootElement.GetProperty("proxyUrl").GetString();
        proxyUrl.ShouldNotBeNullOrEmpty();
        Guid proxyId;
        await using (var context = db.CreateDbContext(tenantId))
        {
            proxyId = (await context.ZaloPersonalAccounts.SingleAsync()).ProxyId!.Value;
        }

        var target = (await SyncAsync()).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("connectionId").GetGuid() == connectionId);
        target.GetProperty("proxyUrl").GetString().ShouldBe(proxyUrl);

        var channels = await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative));
        channels!.Connections.Single().Personal!.Proxy.ShouldBe("active");
        (await owner.GetStringAsync(new Uri("/channels", UriKind.Relative))).ShouldNotContain(proxyUrl!);

        // Quét lại QR cho chính nick đó → cùng userAgent (thiết bị) + cùng proxy.
        using var rescan = await owner.PostAsJsonAsync(new Uri("/channels/zalo-personal/login", UriKind.Relative), new { connectionId });
        rescan.StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = JsonDocument.Parse(_sidecar.Calls.Last(c => c.Path == "/login-qr").Body).RootElement;
        again.GetProperty("userAgent").GetString().ShouldBe("UA-OLD");
        again.GetProperty("proxyUrl").GetString().ShouldBe(proxyUrl);
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.ZaloPersonalAccounts.SingleAsync()).ProxyId.ShouldBe(proxyId);
        }
    }

    [Fact]
    public async Task Failing_proxy_stops_the_nick_and_recovery_restarts_it()
    {
        var admin = await _api.CreateLoggedInClientAsync(await db.CreateSuperAdminAsync());
        await AddProxyAsync(admin, $"http://u:p@flaky-{NewId()}.vn:7000");
        var (tenantId, owner) = await TenantAsync("Proxy chập chờn");
        var connectionId = await ConnectAsync(owner);
        string myProxyUrl;
        await using (var context = db.CreateDbContext(tenantId))
        {
            var proxyId = (await context.ZaloPersonalAccounts.SingleAsync()).ProxyId!.Value;
            using var scope = _api.Services.CreateScope();
            var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
            myProxyUrl = encryptor.Decrypt((await context.Proxies.SingleAsync(p => p.Id == proxyId)).CredentialsEnc);
        }

        // Chỉ proxy của nick này hỏng (proxy khác trong kho vẫn tốt).
        _sidecar.ProxyTestResponse = body => body.Contains(myProxyUrl, StringComparison.Ordinal)
            ? """{"ok":false,"error":"connect ECONNREFUSED"}"""
            : """{"ok":true,"exitIp":"203.0.113.7"}""";
        await RunProxyHealthAsync(); // lần 1: chưa đủ
        await RunProxyHealthAsync(); // lần 2: hỏng → dừng nick

        await using (var context = db.CreateDbContext(tenantId))
        {
            var connection = await context.ChannelConnections.SingleAsync();
            connection.Status.ShouldBe(ConnectionStatus.Disconnected);
            connection.LastError.ShouldBe("zp:proxy_failing");
        }

        (await SyncAsync()).GetProperty("accounts").EnumerateArray()
            .Any(a => a.GetProperty("connectionId").GetGuid() == connectionId).ShouldBeFalse(); // không bao giờ chạy thẳng

        _sidecar.ProxyTestResponse = _ => """{"ok":true,"exitIp":"203.0.113.9"}""";
        await RunProxyHealthAsync();

        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.ChannelConnections.SingleAsync()).Status.ShouldBe(ConnectionStatus.Active);
        }

        (await SyncAsync()).GetProperty("accounts").EnumerateArray()
            .Any(a => a.GetProperty("connectionId").GetGuid() == connectionId).ShouldBeTrue();
    }

    [Fact]
    public async Task Production_style_require_proxy_refuses_new_nicks_when_the_pool_is_empty()
    {
        await using var strict = new ZaloPersonalApiFactory(
            db.ConnectionString, db.RedisConnectionString, _sidecar, new() { ["ZaloPersonal:RequireProxy"] = "true" });
        // Giữ chỗ hết proxy trống hiện có (do test khác thêm) để kho "rỗng" với test này.
        using (var scope = strict.Services.CreateScope())
        {
            var pool = scope.ServiceProvider.GetRequiredService<ProxyPoolService>();
            while (await pool.ReserveFreeAsync(CancellationToken.None) is not null)
            {
            }
        }

        var (tenantId, _) = await db.CreateTenantAsync("Hết IP riêng");
        var owner = await strict.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        using var start = await owner.PostAsync(new Uri("/channels/zalo-personal/login", UriKind.Relative), null);
        await start.ShouldBeProblemAsync(StatusCodes.Status409Conflict, "zalo_personal_no_proxy");
    }

    [Fact]
    public async Task Custom_pacing_needs_risk_confirmation_and_stays_under_the_ceiling()
    {
        var (_, owner) = await TenantAsync("Nhịp gửi tùy chỉnh");
        var connectionId = await ConnectAsync(owner);
        var uri = new Uri($"/channels/zalo-personal/{connectionId}/pacing", UriKind.Relative);

        using (var noConfirm = await owner.PutAsJsonAsync(uri, new { mode = "custom", invitesPerDay = 8, autoBrake = true, confirmRisk = false }))
        {
            noConfirm.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var tooMany = await owner.PutAsJsonAsync(uri, new { mode = "custom", invitesPerDay = 999, autoBrake = true, confirmRisk = true }))
        {
            tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var ok = await owner.PutAsJsonAsync(uri, new { mode = "custom", invitesPerDay = 8, newContactsPerDay = 15, messagesPerDay = 150, autoBrake = false, confirmRisk = true }))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var channels = await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative));
        var info = channels!.Connections.Single().Personal!;
        info.PacingMode.ShouldBe("custom");
        info.AutoBrake.ShouldBeFalse();
        info.Limits!.InvitesPerDay.ShouldBe(3); // nick mới tuần đầu: bậc 3 lời mời dù chọn 8
        info.Limits.MessagesPerDay.ShouldBe(150);
        channels.PersonalPacing!.MaxInvitesPerDay.ShouldBe(20);

        using (var back = await owner.PutAsJsonAsync(uri, new { mode = "safe", autoBrake = false, confirmRisk = false }))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await owner.GetFromJsonAsync<ChannelsResponse>(new Uri("/channels", UriKind.Relative)))!.Connections.Single().Personal!.AutoBrake.ShouldBeTrue();
    }

    [Fact]
    public async Task A_released_proxy_rests_before_another_nick_can_use_it()
    {
        var admin = await _api.CreateLoggedInClientAsync(await db.CreateSuperAdminAsync());
        await AddProxyAsync(admin, $"http://u:p@rest-{NewId()}.vn:6000");
        var (tenantId, owner) = await TenantAsync("Proxy nghỉ");
        var first = await ConnectAsync(owner);
        Guid proxyId;
        await using (var context = db.CreateDbContext(tenantId))
        {
            proxyId = (await context.ZaloPersonalAccounts.SingleAsync(a => a.ConnectionId == first)).ProxyId!.Value;
        }

        (await owner.DeleteAsync(new Uri($"/channels/{first}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var second = await ConnectAsync(owner);

        using var assign = await admin.PutAsJsonAsync(new Uri($"/admin/zalo-personal/{second}/proxy", UriKind.Relative), new { proxyId });
        await assign.ShouldBeProblemAsync(StatusCodes.Status409Conflict, "proxy_cooling");
    }

    [Fact]
    public async Task Staff_cannot_change_pacing()
    {
        var (tenantId, owner) = await TenantAsync("Nhân viên không đổi nhịp");
        var connectionId = await ConnectAsync(owner);
        var staff = await _api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, ZaloAi.Core.Tenancy.TenantRole.Staff));
        using var response = await staff.PutAsJsonAsync(
            new Uri($"/channels/zalo-personal/{connectionId}/pacing", UriKind.Relative), new { mode = "custom", invitesPerDay = 20, autoBrake = false, confirmRisk = true });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bot_reply_over_the_daily_limit_goes_to_staff_instead()
    {
        var (tenantId, owner) = await TenantAsync("Vượt giới hạn tin");
        var connectionId = await ConnectAsync(owner);
        using (var pacing = await owner.PutAsJsonAsync(
                   new Uri($"/channels/zalo-personal/{connectionId}/pacing", UriKind.Relative),
                   new { mode = "custom", messagesPerDay = 1, autoBrake = true, confirmRisk = true }))
        {
            pacing.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var customer = NewId();
        using (var e = await InternalAsync("/internal/zalo-personal/events", new
        {
            connectionId,
            events = new[] { new { type = "message", msgId = "lim-1", threadId = customer, fromUid = customer, isSelf = false, isFriend = false, kind = "text", text = "hi", ts = 1L } },
        }))
        {
            e.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var scope = _api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, null, null, false);
            var conversations = scope.ServiceProvider.GetRequiredService<ConversationRepository>();
            var delivery = scope.ServiceProvider.GetRequiredService<MessageDeliveryService>();
            var limiter = scope.ServiceProvider.GetRequiredService<PersonalSendLimiter>();
            await limiter.CountAsync(connectionId, PacingCounter.Messages, CancellationToken.None); // đã dùng hết 1 tin hôm nay

            var conversation = (await conversations.ListConversationsAsync(tenantId, isTest: false, limit: 5, CancellationToken.None)).Single();
            var tracked = (await conversations.GetConversationAsync(tenantId, conversation.Id, CancellationToken.None))!;
            var contact = (await conversations.GetContactAsync(tenantId, tracked.ContactId, CancellationToken.None))!;
            var reply = delivery.AddOutgoing(tenantId, tracked, MessageSender.Bot, "Dạ em chào chị");
            await conversations.SaveChangesAsync(CancellationToken.None);
            await delivery.DeliverAsync(tenantId, tracked, contact, reply, CancellationToken.None);
        }

        await using var context = db.CreateDbContext(tenantId);
        var bot = await context.Messages.SingleAsync(m => m.Sender == MessageSender.Bot);
        bot.DeliveryStatus.ShouldBe(DeliveryStatus.Failed);
        bot.DeliveryError.ShouldBe("zp:send_limit");
        var after = await context.Conversations.SingleAsync();
        after.Mode.ShouldBe(ConversationMode.Human);
        after.HandoffReason.ShouldBe(MessageDeliveryService.SendLimitReason);
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/send");
    }
}
