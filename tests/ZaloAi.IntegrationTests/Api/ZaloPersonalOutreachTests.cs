using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Connections;
using ZaloAi.Api.Inbox;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Z3: ô dán SĐT (nguồn + xác nhận), hàng đợi tìm Zalo → tin chào đầu / mời kết bạn, giới hạn, từ chối, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class ZaloPersonalOutreachTests(PostgresFixture db) : IAsyncLifetime, IDisposable
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

    /// <summary>SĐT di động hợp lệ, ngẫu nhiên (tránh trùng giữa các test cùng DB). <paramref name="tail"/> = 3 số cuối.</summary>
    private static string Phone(string? tail = null)
    {
        var body = Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return tail is null ? $"09{body}1" : $"09{body[..5]}{tail}";
    }

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

    private async Task<(Guid TenantId, HttpClient Owner, Guid ConnectionId)> ConnectedTenantAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await _api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        return (tenantId, owner, await LoginNickAsync(owner));
    }

    /// <summary>Đăng nhập thêm một nick (QR giả) cho DN của <paramref name="owner"/>; trả id kết nối.</summary>
    private async Task<Guid> LoginNickAsync(HttpClient owner, string displayName = "Nick shop")
    {
        using var start = await owner.PostAsync(new Uri("/channels/zalo-personal/login", UriKind.Relative), null);
        var loginId = (await start.Content.ReadFromJsonAsync<PersonalLoginStartResponse>())!.LoginId;
        var uid = Random.Shared.NextInt64(1_000_000_000, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var result = await InternalAsync("/internal/zalo-personal/login-result", new
        {
            loginId,
            uid,
            displayName,
            session = new { cookie = new[] { new { key = "k", value = "v" } }, imei = "i", userAgent = "u" },
        });
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionId").GetGuid();
    }

    private static async Task<OutreachBatchResponse> PasteAsync(HttpClient client, string phones, string origin = "referral", string? referrer = "chị Lan", string? note = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/zalo-outreach/batches", UriKind.Relative), new { phones, origin, note, referrerName = referrer, attested = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OutreachBatchResponse>())!;
    }

    private static async Task<List<OutreachTargetResponse>> TargetsAsync(HttpClient client, string? state = null) =>
        (await client.GetFromJsonAsync<List<OutreachTargetResponse>>(
            new Uri(state is null ? "/zalo-outreach/targets" : $"/zalo-outreach/targets?state={state}", UriKind.Relative)))!;

    /// <summary>Chạy job hàng đợi với đồng hồ cố định: trong khung giờ được nhắn và SAU lúc dán (để người vừa dán đã tới lượt).</summary>
    private async Task RunOutreachAsync(Guid tenantId, TimeSpan? after = null)
    {
        using var scope = _api.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<OutreachTenantJob>(scope.ServiceProvider, new FixedClock(WindowTime(after)));
        await job.RunAsync(tenantId, CancellationToken.None);
    }

    /// <summary>Mốc = lúc sớm nhất trong khung giờ được nhắn (chạy test ban đêm → sáng mai), rồi cộng "after": lượt sau luôn muộn hơn lượt trước.</summary>
    private static DateTimeOffset WindowTime(TimeSpan? after = null)
    {
        var window = new HandoffSettings { CareSendStart = "08:00", CareSendEnd = "20:00" };
        var t = DateTimeOffset.UtcNow;
        while (!CareDecision.InSendWindow(window, t))
        {
            t = t.AddMinutes(30);
        }

        t = t.Add(after ?? TimeSpan.FromMinutes(1));
        while (!CareDecision.InSendWindow(window, t))
        {
            t = t.AddMinutes(30);
        }

        return t;
    }

    private async Task MaintenanceAsync(Guid tenantId)
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OutreachMaintenanceJob>().RunAsync(tenantId, CancellationToken.None);
    }

    private async Task EventsAsync(Guid connectionId, params object[] events)
    {
        using var response = await InternalAsync("/internal/zalo-personal/events", new { connectionId, events });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Đặt số lời mời/ngày của nick. 1 = người mới KHÔNG được mời (lượt duy nhất giữ cho người đã trả lời) → chỉ nhắn, mời khi khách trả
    /// lời; mặc định (nick mới tuần đầu: 3, người mới dùng 2) → nhắn trước rồi vài phút sau mời kết bạn (chủ dự án chốt 2026-11-25).
    /// </summary>
    private static async Task InvitesPerDayAsync(HttpClient owner, Guid connectionId, int invites, int? newContacts = null)
    {
        using var pacing = await owner.PutAsJsonAsync(
            new Uri($"/channels/zalo-personal/{connectionId}/pacing", UriKind.Relative),
            new { mode = "custom", invitesPerDay = invites, newContactsPerDay = newContacts, autoBrake = true, confirmRisk = true });
        pacing.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<List<string>> ProactiveTextsAsync(Guid tenantId)
    {
        using var scope = _api.Services.CreateScope();
        var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
        await using var context = db.CreateDbContext(tenantId);
        return (await context.Messages.Where(m => m.Proactive).OrderBy(m => m.CreatedAt).ToListAsync())
            .Select(m => encryptor.Decrypt(m.ContentEnc)).ToList();
    }

    [Fact]
    public async Task Paste_needs_attestation_and_skips_bad_duplicate_and_opted_out_numbers()
    {
        var (tenantId, owner, _) = await ConnectedTenantAsync("Dán SĐT");

        using (var noAttest = await owner.PostAsJsonAsync(new Uri("/zalo-outreach/batches", UriKind.Relative), new { phones = Phone(), origin = "referral", attested = false }))
        {
            noAttest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var optedOut = Phone();
        await PasteAsync(owner, optedOut);
        await using (var context = db.CreateDbContext(tenantId))
        {
            var contact = await context.Contacts.SingleAsync();
            contact.ProactiveOptOutAt = DateTimeOffset.UtcNow;
            var target = await context.OutreachTargets.SingleAsync();
            target.State = OutreachState.Declined;
            await context.SaveChangesAsync();
        }

        var a = Phone();
        var b = Phone();
        var result = await PasteAsync(owner, $"{a}, chị Hoa\n{a}\n123\n{b}\n{optedOut}", note: "quan tâm liệu trình giảm mỡ");

        result.Accepted.ShouldBe(2);
        result.Skipped.Select(s => s.Reason).OrderBy(r => r, StringComparer.Ordinal).ShouldBe(["duplicate", "invalid", "opted_out"]);
        result.EtaDays.ShouldBeGreaterThanOrEqualTo(1);

        var queued = await TargetsAsync(owner, "queued");
        queued.Count.ShouldBe(2);
        queued.ShouldAllBe(t => t.PhoneMasked!.Contains("***"));
        queued.Single(t => t.Name == "chị Hoa").Origin.ShouldBe("referral");

        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.OutreachBatches.CountAsync()).ShouldBe(2);
            (await context.ContactNotes.CountAsync()).ShouldBe(2); // ghi chú cho AI của đợt gắn vào từng người
        }

        var batches = await owner.GetFromJsonAsync<List<OutreachBatchItemResponse>>(new Uri("/zalo-outreach/batches", UriKind.Relative));
        batches!.Count.ShouldBe(2);
        batches[0].AcceptedCount.ShouldBe(2); // mới nhất trước
        batches[0].Origin.ShouldBe("referral");
    }

    [Fact]
    public async Task Found_number_gets_the_permission_asking_greeting_and_reply_marks_it_consulting()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Hành trình nhắn thẳng");
        await InvitesPerDayAsync(owner, connectionId, 1); // hết lượt mời cho người mới → nhắn trước
        var phone = Phone();
        await PasteAsync(owner, $"{phone}, Hoa");

        await RunOutreachAsync(tenantId);

        var send = _sidecar.Calls.Single(c => c.Path == $"/accounts/{connectionId}/send");
        var body = JsonDocument.Parse(send.Body).RootElement;
        body.GetProperty("threadId").GetString().ShouldBe($"u-{phone}");
        var text = body.GetProperty("text").GetString()!; // AI viết (test: AI giả) — luật: báo trợ lý AI, hỏi xin phép, không số
        text.ShouldContain("trợ lý AI");
        text.ShouldContain("không ạ?");
        text.Any(char.IsDigit).ShouldBeFalse(); // không kèm giá / số tiền trong tin đầu
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/friend-request");

        var target = (await TargetsAsync(owner)).Single();
        target.State.ShouldBe("messaged");
        await using (var context = db.CreateDbContext(tenantId))
        {
            var contact = await context.Contacts.SingleAsync();
            contact.Channel.ShouldBe(ChannelKind.ZaloPersonal);
            contact.ExternalUserId.ShouldBe($"u-{phone}");
            contact.Origin.ShouldBe("referral");
            contact.ProactiveAwaitingReply.ShouldBeTrue();
            (await context.Messages.SingleAsync()).Proactive.ShouldBeTrue();
        }

        await EventsAsync(connectionId, new
        {
            type = "message",
            msgId = "rep-1",
            threadId = $"u-{phone}",
            fromUid = $"u-{phone}",
            isSelf = false,
            isFriend = false,
            kind = "text",
            text = "có em ơi",
            ts = 1L,
        });
        (await TargetsAsync(owner)).Single().State.ShouldBe("replied");

        // Khách đã trả lời mà chưa là bạn bè → vài phút sau gửi MỘT lời mời kết bạn (chủ dự án chốt 2026-11-14), vẫn là "đang tư vấn".
        // Hẹn mời sau vài phút (không mời ngay giữa lúc đang trò chuyện) — kiểm thẳng giờ hẹn, không phụ thuộc giờ chạy test.
        (await TargetsAsync(owner)).Single().NextActionAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(2));
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/friend-request");
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(10));
        var invite = JsonDocument.Parse(_sidecar.Calls.Single(c => c.Path == $"/accounts/{connectionId}/friend-request").Body).RootElement;
        invite.GetProperty("uid").GetString().ShouldBe($"u-{phone}");
        var inviteText = invite.GetProperty("message").GetString()!;
        inviteText.ShouldContain("trợ lý AI");
        inviteText.ShouldContain("Kết bạn");
        inviteText.Length.ShouldBeLessThanOrEqualTo(150);
        (await TargetsAsync(owner)).Single().State.ShouldBe("replied");

        await EventsAsync(connectionId, new { type = "message", msgId = "rep-2", threadId = $"u-{phone}", fromUid = $"u-{phone}", isSelf = false, isFriend = false, kind = "text", text = "ok", ts = 2L });
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(30));
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1); // mỗi người chỉ mời một lần
    }

    [Fact]
    public async Task Reply_from_someone_already_a_friend_sends_no_invite()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Đã là bạn bè");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var phone = Phone();
        await PasteAsync(owner, phone);
        await RunOutreachAsync(tenantId);

        await EventsAsync(connectionId, new { type = "message", msgId = "fr-1", threadId = $"u-{phone}", fromUid = $"u-{phone}", isSelf = false, isFriend = true, kind = "text", text = "dạ", ts = 1L });
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(10));

        (await TargetsAsync(owner)).Single().State.ShouldBe("replied");
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/friend-request");
    }

    [Fact]
    public async Task Greeting_first_then_invite_then_one_intro_after_accepting()
    {
        // Chủ dự án chốt 2026-11-25: có Zalo → NHẮN TRƯỚC, vài phút sau MỜI KẾT BẠN luôn; chấp nhận mà chưa trả lời → MỘT tin cảm ơn +
        // giới thiệu doanh nghiệp + câu hỏi mở (không "cần gì cứ nhắn").
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Nhắn rồi kết bạn");
        var phone = Phone();
        await PasteAsync(owner, $"{phone}, chị Hoa");

        await RunOutreachAsync(tenantId);

        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(1); // tin chào đi trước
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/friend-request"); // chưa mời ngay
        var messaged = (await TargetsAsync(owner)).Single();
        messaged.State.ShouldBe("messaged");
        messaged.NextActionAt.ShouldNotBeNull(); // hẹn mời kết bạn sau 2–5 phút

        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(6));
        var invite = JsonDocument.Parse(_sidecar.Calls.Single(c => c.Path == $"/accounts/{connectionId}/friend-request").Body).RootElement;
        invite.GetProperty("uid").GetString().ShouldBe($"u-{phone}");
        var inviteText = invite.GetProperty("message").GetString()!;
        inviteText.ShouldContain("Kết bạn");
        inviteText.Length.ShouldBeLessThanOrEqualTo(150);
        (await TargetsAsync(owner)).Single().State.ShouldBe("messaged"); // tin chào đã tới khách: vẫn "đã nhắn"

        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(30)); // không mời lần hai, không nhắn thêm khi chưa chấp nhận
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(1);

        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 1L });
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(45));
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(60));
        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 2L }); // xóa rồi kết bạn lại
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(90));

        var texts = await ProactiveTextsAsync(tenantId);
        texts.Count.ShouldBe(2); // tin chào + đúng MỘT tin giới thiệu
        texts[1].ShouldContain("cảm ơn");
        texts[1].ShouldEndWith("ạ?"); // kết bằng câu hỏi mở để khách trả lời
        texts[1].ShouldNotContain("cứ nhắn");
        (await TargetsAsync(owner)).Single().State.ShouldBe("messaged");
    }

    [Fact]
    public async Task New_people_use_the_invites_left_for_them_and_the_rest_are_invited_another_day()
    {
        // 2 lời mời/ngày: 1 giữ cho người đã trả lời, 1 cho người mới → cả hai được nhắn; chỉ người thứ nhất được mời hôm nay.
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Hết lượt mời thì mời sau");
        await InvitesPerDayAsync(owner, connectionId, 2);
        await PasteAsync(owner, $"{Phone()}\n{Phone()}");

        await RunOutreachAsync(tenantId);
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(6));
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(30)); // qua khoảng cách 10–20 phút giữa 2 lời mời

        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(2);
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);
        var targets = await TargetsAsync(owner);
        targets.ShouldAllBe(t => t.State == "messaged");
        targets.Count(t => t.NextActionAt > DateTimeOffset.UtcNow.AddHours(1)).ShouldBe(1); // người còn lại: mời vào ngày còn lượt
    }

    [Fact]
    public async Task Accepting_after_chatting_gets_one_thank_you()
    {
        // Nhắn trước → khách trả lời → mời kết bạn sau → khách chấp nhận → đúng MỘT câu cảm ơn + giới thiệu sơ (2026-11-15).
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Cảm ơn kết bạn");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var phone = Phone();
        await PasteAsync(owner, phone);
        await RunOutreachAsync(tenantId);
        await EventsAsync(connectionId, new { type = "message", msgId = "cam-on-1", threadId = $"u-{phone}", fromUid = $"u-{phone}", isSelf = false, isFriend = false, kind = "text", text = "ừ gửi đi em", ts = 1L });
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(10));
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);

        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 2L });
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(20));
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(40));

        // Sự kiện "kết bạn" đến lại (khách xóa rồi kết bạn lại / nick nối lại) → KHÔNG cảm ơn lần hai (2026-11-27).
        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 3L });
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(60));
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(80));

        var texts = await ProactiveTextsAsync(tenantId);
        texts.Count.ShouldBe(2); // tin chào + đúng 1 câu cảm ơn
        texts.Count(t => t.Contains("cảm ơn", StringComparison.Ordinal)).ShouldBe(1);
        (await TargetsAsync(owner)).Single().State.ShouldBe("replied");
    }

    [Fact]
    public async Task Blocked_stranger_waits_for_an_invite_left_for_new_people()
    {
        // Nhắn trước bị chặn tin người lạ mà hôm nay không còn lượt mời cho người mới → đợi ngày sau, không gửi gì thêm.
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Chặn người lạ");
        await InvitesPerDayAsync(owner, connectionId, 1);
        _sidecar.SendResponse = _ => (HttpStatusCode.UnprocessableEntity, """{"error":"rejected","code":114,"reason":"stranger"}""");
        await PasteAsync(owner, Phone());

        await RunOutreachAsync(tenantId);
        await RunOutreachAsync(tenantId);

        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(1);
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/friend-request");
        var target = (await TargetsAsync(owner)).Single();
        target.State.ShouldBe("queued");
        target.NextActionAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task A_friend_whose_message_is_still_rejected_fails_once_instead_of_retrying()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Bạn bè vẫn bị từ chối");
        var phone = Phone();
        _sidecar.SendResponse = _ => (HttpStatusCode.UnprocessableEntity, """{"error":"rejected","code":114,"reason":"stranger"}""");
        await PasteAsync(owner, phone);
        await RunOutreachAsync(tenantId); // nhắn trước bị chặn tin người lạ → kết bạn kèm lời chào
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);
        (await TargetsAsync(owner)).Single().State.ShouldBe("invited");
        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 1L });

        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(5));
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(10));

        (await TargetsAsync(owner)).Single().State.ShouldBe("failed");
        // Tin chào lần đầu + đúng 1 lần thử nhắn sau khi kết bạn; không gửi lại ở các lượt sau.
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(2);
    }

    [Fact]
    public async Task Rejecting_the_invite_means_never_contacted_again()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Từ chối kết bạn");
        var phone = Phone();
        _sidecar.SendResponse = _ => (HttpStatusCode.UnprocessableEntity, """{"error":"rejected","code":114}""");
        await PasteAsync(owner, phone);
        await RunOutreachAsync(tenantId);

        await EventsAsync(connectionId, new { type = "friend", @event = "reject_request", uid = $"u-{phone}", ts = 1L });

        (await TargetsAsync(owner)).Single().State.ShouldBe("declined");
        var again = await PasteAsync(owner, phone);
        again.Accepted.ShouldBe(0);
        again.Skipped.Single().Reason.ShouldBe("opted_out");
    }

    [Fact]
    public async Task Number_without_zalo_goes_to_the_call_list()
    {
        var (tenantId, owner, _) = await ConnectedTenantAsync("Không có Zalo");
        await PasteAsync(owner, Phone("000"));

        await RunOutreachAsync(tenantId);

        (await TargetsAsync(owner)).Single().State.ShouldBe("nozalo");
        _sidecar.Calls.ShouldNotContain(c => c.Path.EndsWith("/send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Daily_new_contact_limit_spreads_the_queue_over_days()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Giới hạn người mới");
        using (var pacing = await owner.PutAsJsonAsync(
                   new Uri($"/channels/zalo-personal/{connectionId}/pacing", UriKind.Relative),
                   new { mode = "custom", newContactsPerDay = 1, autoBrake = true, confirmRisk = true }))
        {
            pacing.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var result = await PasteAsync(owner, $"{Phone()}\n{Phone()}\n{Phone()}");
        result.EtaDays.ShouldBe(3);

        await RunOutreachAsync(tenantId);

        var targets = await TargetsAsync(owner);
        targets.Count(t => t.State is "messaged" or "invited").ShouldBe(1); // lời mời đầu tiên cũng là "người mới"
        targets.Where(t => t.State == "queued").ShouldAllBe(t => t.NextActionAt > DateTimeOffset.UtcNow.AddHours(1)); // dời sang ngày sau
    }

    [Fact]
    public async Task Summary_tells_when_the_queue_starts_again_outside_send_hours()
    {
        var (_, owner, connectionId) = await ConnectedTenantAsync("Ngoài khung giờ");
        await PasteAsync(owner, Phone());

        // Chọn khung giờ KHÔNG chứa lúc chạy test (luôn trong 07:00–21:00 hệ thống cho phép).
        var hour = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Hour;
        var (start, end) = hour is >= 7 and < 14 ? ("20:00", "21:00") : ("07:00", "07:30");
        var s = (await owner.GetFromJsonAsync<HandoffSettingsResponse>(new Uri("/tenant/handoff-settings", UriKind.Relative)))!;
        using (var put = await owner.PutAsJsonAsync(new Uri("/tenant/handoff-settings", UriKind.Relative), new UpdateHandoffSettingsRequest(
                   s.HandoffMessage, s.AfterHoursMessage, s.TakeoverMessageEnabled, s.TakeoverMessage, s.ReturnToBotMessageEnabled, s.ReturnToBotMessage,
                   s.StaffSignatureEnabled, s.ResponseTime, s.OpenTime, s.CloseTime, s.WorkingDays, s.ReminderMinutes, s.TelegramChatId,
                   s.CareEnabled, s.CareColdHours, s.CareAutoSend, start, end)))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var summary = (await owner.GetFromJsonAsync<List<OutreachSummaryResponse>>(new Uri("/zalo-outreach/summary", UriKind.Relative), Web))!
            .Single(x => x.ConnectionId == connectionId);

        summary.Queued.ShouldBe(1);
        var next = summary.NextSendAt.ShouldNotBeNull().ToOffset(TimeSpan.FromHours(7));
        next.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(start);
        next.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        next.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(1));
    }

    private static async Task<OutreachBatchResponse> DistributeAsync(HttpClient client, string phones, bool dryRun, List<Guid>? connectionIds = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/zalo-outreach/batches", UriKind.Relative),
            new { phones, origin = "customer", attested = !dryRun, distribute = true, connectionIds, dryRun });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OutreachBatchResponse>())!;
    }

    [Fact]
    public async Task Distribute_splits_by_nick_capacity_and_preview_saves_nothing()
    {
        var (_, owner, nickA) = await ConnectedTenantAsync("Chia nhiều nick");
        var nickB = await LoginNickAsync(owner, "Nick hai");
        await InvitesPerDayAsync(owner, nickA, invites: 3, newContacts: 20);
        await InvitesPerDayAsync(owner, nickB, invites: 3, newContacts: 10);
        var phones = string.Join("\n", Enumerable.Range(0, 6).Select(_ => Phone()));

        var preview = await DistributeAsync(owner, phones, dryRun: true);
        preview.DryRun.ShouldBeTrue();
        preview.BatchId.ShouldBeNull();
        preview.Accepted.ShouldBe(6);
        preview.Allocations.Single(a => a.ConnectionId == nickA).Assigned.ShouldBe(4); // 20/ngày nhận gấp đôi 10/ngày
        preview.Allocations.Single(a => a.ConnectionId == nickB).Assigned.ShouldBe(2);
        (await TargetsAsync(owner)).ShouldBeEmpty(); // xem trước: không lưu gì

        var created = await DistributeAsync(owner, phones, dryRun: false);
        created.BatchId.ShouldNotBeNull();
        var targets = await TargetsAsync(owner);
        targets.Count(t => t.ConnectionId == nickA).ShouldBe(4);
        targets.Count(t => t.ConnectionId == nickB).ShouldBe(2);

        // Chỉ chia cho nick B.
        var onlyB = await DistributeAsync(owner, Phone(), dryRun: true, [nickB]);
        onlyB.Allocations.ShouldHaveSingleItem().ConnectionId.ShouldBe(nickB);
    }

    [Fact]
    public async Task Distribute_without_a_running_nick_is_a_conflict()
    {
        var (tenantId, owner, nickA) = await ConnectedTenantAsync("Không nick chạy");
        await using (var context = db.CreateDbContext(tenantId))
        {
            var account = await context.ZaloPersonalAccounts.SingleAsync(a => a.ConnectionId == nickA);
            account.OutreachLevel = OutreachLevel.Paused; // phanh tạm dừng → không nhận người mới
            await context.SaveChangesAsync();
        }

        using var response = await owner.PostAsJsonAsync(
            new Uri("/zalo-outreach/batches", UriKind.Relative), new { phones = Phone(), origin = "customer", attested = true, distribute = true });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Move_only_waiting_people_and_never_to_another_tenants_nick()
    {
        var (tenantId, owner, nickA) = await ConnectedTenantAsync("Chuyển nick");
        var nickB = await LoginNickAsync(owner, "Nick hai");
        await PasteAsync(owner, $"{Phone()}\n{Phone()}");
        var targets = await TargetsAsync(owner);
        targets.ShouldAllBe(t => t.ConnectionId == nickA);
        await using (var context = db.CreateDbContext(tenantId))
        {
            var contacted = await context.OutreachTargets.SingleAsync(t => t.Id == targets[0].Id);
            contacted.State = OutreachState.Messaged; // nick A đã nhắn người này
            await context.SaveChangesAsync();
        }

        using (var move = await owner.PutAsJsonAsync(
                   new Uri("/zalo-outreach/targets", UriKind.Relative), new { ids = targets.Select(t => t.Id).ToList(), stop = false, moveToConnectionId = nickB }))
        {
            move.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await move.Content.ReadFromJsonAsync<int>()).ShouldBe(1);
        }

        var after = await TargetsAsync(owner);
        after.Single(t => t.Id == targets[0].Id).ConnectionId.ShouldBe(nickA); // đã được liên hệ → giữ nick cũ
        after.Single(t => t.Id == targets[1].Id).ConnectionId.ShouldBe(nickB);

        // Cô lập tenant: không chuyển được sang nick của DN khác.
        var (_, _, otherNick) = await ConnectedTenantAsync("DN khác");
        using var foreign = await owner.PutAsJsonAsync(
            new Uri("/zalo-outreach/targets", UriKind.Relative), new { ids = new[] { targets[1].Id }, stop = false, moveToConnectionId = otherNick });
        foreign.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TargetsAsync(owner)).Single(t => t.Id == targets[1].Id).ConnectionId.ShouldBe(nickB);
    }

    [Fact]
    public async Task Redistribute_moves_a_nicks_waiting_people_to_other_running_nicks()
    {
        var (_, owner, nickA) = await ConnectedTenantAsync("Chia lại hàng chờ");
        var nickB = await LoginNickAsync(owner, "Nick hai");
        await PasteAsync(owner, $"{Phone()}\n{Phone()}\n{Phone()}");

        using var response = await owner.PostAsync(new Uri($"/zalo-outreach/nicks/{nickA}/redistribute", UriKind.Relative), null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<List<OutreachAllocationResponse>>())!;
        result.ShouldHaveSingleItem().Assigned.ShouldBe(3);
        (await TargetsAsync(owner)).ShouldAllBe(t => t.ConnectionId == nickB);

        var summary = (await owner.GetFromJsonAsync<List<OutreachSummaryResponse>>(new Uri("/zalo-outreach/summary", UriKind.Relative), Web))!;
        summary.Single(s => s.ConnectionId == nickB).StateCounts["queued"].ShouldBe(3);
        summary.Single(s => s.ConnectionId == nickA).StateCounts.ShouldBeEmpty();
    }

    /// <summary>Kiểm tra hàng chờ của DN (như cuối job hàng đợi) → các tin Telegram vừa xếp hàng gửi.</summary>
    private async Task<List<string>> WatchAsync(Guid tenantId)
    {
        using var scope = _api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ZaloAi.Infrastructure.Tenancy.TenantContext>().Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var sent = await scope.ServiceProvider.GetRequiredService<ZaloAi.Infrastructure.ZaloPersonal.OutreachQueueWatcher>().CheckAsync(tenantId, CancellationToken.None);
        return sent.Select(a => a.Reason).ToList();
    }

    /// <summary>Đánh dấu <paramref name="count"/> người đang chờ là đã được nhắn (giả như job đã chạy).</summary>
    private async Task ContactAsync(Guid tenantId, int count)
    {
        await using var context = db.CreateDbContext(tenantId);
        foreach (var t in await context.OutreachTargets.Where(t => t.State == OutreachState.Queued).OrderBy(t => t.Id).Take(count).ToListAsync())
        {
            t.State = OutreachState.Messaged;
            t.NextActionAt = null;
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Telegram_alerts_once_when_a_nicks_list_runs_low_and_once_when_it_runs_out()
    {
        var (tenantId, owner, nick) = await ConnectedTenantAsync("Báo hết danh sách");
        await InvitesPerDayAsync(owner, nick, invites: 1, newContacts: 2); // nhận 2 người mới/ngày
        (await WatchAsync(tenantId)).ShouldBeEmpty(); // nick chưa từng được giao số

        await PasteAsync(owner, string.Join("\n", Enumerable.Range(0, 4).Select(_ => Phone())));
        (await WatchAsync(tenantId)).ShouldBeEmpty(); // 4 người chờ > 2/ngày

        await ContactAsync(tenantId, 2);
        (await WatchAsync(tenantId)).ShouldBe(["zp:queue_low"]); // còn 2 = khoảng 1 ngày
        (await WatchAsync(tenantId)).ShouldBeEmpty(); // chỉ báo một lần

        await ContactAsync(tenantId, 2);
        (await WatchAsync(tenantId)).ShouldBe(["zp:queue_empty"]);
        (await WatchAsync(tenantId)).ShouldBeEmpty();

        // Dán thêm → lần hết sau báo lại.
        await PasteAsync(owner, string.Join("\n", Enumerable.Range(0, 3).Select(_ => Phone())));
        (await WatchAsync(tenantId)).ShouldBeEmpty();
        await ContactAsync(tenantId, 3);
        (await WatchAsync(tenantId)).ShouldBe(["zp:queue_empty"]);
    }

    [Fact]
    public async Task A_short_list_is_not_reported_as_running_low_right_after_pasting()
    {
        var (tenantId, owner, nick) = await ConnectedTenantAsync("Danh sách ngắn");
        await InvitesPerDayAsync(owner, nick, invites: 1, newContacts: 5);

        await PasteAsync(owner, $"{Phone()}\n{Phone()}");
        (await WatchAsync(tenantId)).ShouldBeEmpty(); // 2 người ≤ 5/ngày nhưng hàng chờ chưa từng dài → không báo "sắp hết"

        await ContactAsync(tenantId, 2);
        (await WatchAsync(tenantId)).ShouldBe(["zp:queue_empty"]);
    }

    [Fact]
    public async Task A_quiet_day_after_our_last_message_moves_to_the_call_list_and_opt_out_declines()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Dọn hàng đợi");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var quiet = Phone();
        var optOut = Phone();
        await PasteAsync(owner, $"{quiet}\n{optOut}");
        await RunOutreachAsync(tenantId);

        // Tin chào 3 ngày trước nhưng 12 giờ trước bên mình vừa nhắn thêm (tin giới thiệu / nhân viên) → chưa tính là im 24 giờ.
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromHours(12));
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.Contacts.SingleAsync(c => c.ExternalUserId == $"u-{optOut}")).ProactiveOptOutAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await MaintenanceAsync(tenantId);
        (await TargetsAsync(owner)).Select(t => t.State).OrderBy(s => s, StringComparer.Ordinal).ShouldBe(["declined", "messaged"]);

        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromHours(25));
        await MaintenanceAsync(tenantId);
        (await TargetsAsync(owner)).Select(t => t.State).OrderBy(s => s, StringComparer.Ordinal).ShouldBe(["declined", "noreply"]);

        var calls = await CallListAsync(owner);
        calls.Single().Phone.ShouldBe(quiet); // SĐT đầy đủ để bấm gọi
        calls.Single().Reason.ShouldBe("noreply");
        calls.Single().NickName.ShouldBe("Nick shop");
        var count = (await owner.GetFromJsonAsync<ZaloAi.Api.Customers.CareCountResponse>(new Uri("/care/count", UriKind.Relative)))!;
        count.ToCall.ShouldBe(1);
        count.ToCallWarm.ShouldBe(0);
        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.AuditLogs.CountAsync(a => a.TenantId == tenantId && a.Action == "care.call_list_viewed")).ShouldBe(1); // xem SĐT đầy đủ → ghi nhật ký
        }

        // Khách trả lời muộn → về "Đang tư vấn", rời danh sách gọi (không để nhân viên gọi người đang chat với bot).
        await EventsAsync(connectionId, new { type = "message", msgId = "late-1", threadId = $"u-{quiet}", fromUid = $"u-{quiet}", isSelf = false, isFriend = false, kind = "text", text = "giờ mới đọc tin em", ts = 1L });
        (await TargetsAsync(owner)).Single(t => t.State != "declined").State.ShouldBe("replied");
        (await CallListAsync(owner)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Consulting_customer_quiet_for_two_weeks_goes_cold_and_tops_the_call_list()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Khách nguội");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var warm = Phone();
        var quiet = Phone();
        await PasteAsync(owner, $"{warm}, chị Lan\n{quiet}, anh Bình");
        await RunOutreachAsync(tenantId);
        await EventsAsync(connectionId, new { type = "message", msgId = "w-1", threadId = $"u-{warm}", fromUid = $"u-{warm}", isSelf = false, isFriend = true, kind = "text", text = "gói cổ vai gáy bao nhiêu em", ts = 1L });

        // Chị Lan hỏi giá rồi im 15 ngày; anh Bình nhận tin chào rồi im 3 ngày.
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(16), lastMessage: TimeSpan.FromDays(15), customerMessage: TimeSpan.FromDays(15));
        await MaintenanceAsync(tenantId);

        var states = (await TargetsAsync(owner)).ToDictionary(t => t.Name!, t => t.State);
        states["chị Lan"].ShouldBe("cold");
        states["anh Bình"].ShouldBe("noreply");
        var calls = await CallListAsync(owner);
        calls.Select(c => c.Name).ShouldBe(["chị Lan", "anh Bình"]); // từng tư vấn → gọi trước
        calls[0].Reason.ShouldBe("cold");
        calls[0].LastCustomerText.ShouldBe("gói cổ vai gáy bao nhiêu em");
        calls[1].LastCustomerText.ShouldBeNull();
        (await owner.GetFromJsonAsync<ZaloAi.Api.Customers.CareCountResponse>(new Uri("/care/count", UriKind.Relative)))!.ToCallWarm.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_appointment_converts_and_thirty_untouched_days_archive()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Thành khách");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var booked = Phone();
        var forgotten = Phone();
        await PasteAsync(owner, $"{booked}, chị Hoa\n{forgotten}, anh Tư");
        await RunOutreachAsync(tenantId);
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromDays(3));
        await MaintenanceAsync(tenantId);
        (await CallListAsync(owner)).Count.ShouldBe(2);

        // Nhân viên gọi chị Hoa, đặt lịch qua điện thoại → đã thành khách, rời danh sách gọi.
        await using (var context = db.CreateDbContext(tenantId))
        {
            var branch = await context.Branches.FirstOrDefaultAsync();
            if (branch is null)
            {
                branch = new Branch { TenantId = tenantId, Code = "CN1", Name = "Chi nhánh 1", IsDefault = true };
                context.Branches.Add(branch);
            }

            var contact = await context.Contacts.SingleAsync(c => c.ExternalUserId == $"u-{booked}");
            context.Appointments.Add(new Appointment
            {
                TenantId = tenantId,
                BranchId = branch.Id,
                ContactId = contact.Id,
                StartAt = DateTimeOffset.UtcNow.AddDays(2),
                EndAt = DateTimeOffset.UtcNow.AddDays(2).AddHours(1),
            });
            await context.SaveChangesAsync();

            // Anh Tư nằm trong danh sách 31 ngày không ai xử lý.
            await context.OutreachTargets.Where(t => t.ContactId != contact.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow.AddDays(-31)));
        }

        await MaintenanceAsync(tenantId);

        var states = (await TargetsAsync(owner)).ToDictionary(t => t.Name!, t => t.State);
        states["chị Hoa"].ShouldBe("converted");
        states["anh Tư"].ShouldBe("archived");
        (await CallListAsync(owner)).ShouldBeEmpty();
        (await owner.GetFromJsonAsync<OutreachSummaryResponse[]>(new Uri("/zalo-outreach/summary", UriKind.Relative)))!
            .Single().Booked.ShouldBe(1); // người đã thành khách vẫn tính "có lịch hẹn"
    }

    [Fact]
    public async Task Call_results_move_people_and_are_kept_in_the_care_log()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Kết quả gọi");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var staff = await _api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, ZaloAi.Core.Tenancy.TenantRole.Staff));
        await PasteAsync(owner, $"{Phone()}, Quan Tâm\n{Phone()}, Không Mua\n{Phone()}, Không Nghe");
        await RunOutreachAsync(tenantId);
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromDays(3));
        await MaintenanceAsync(tenantId);
        var ids = (await CallListAsync(staff)).ToDictionary(c => c.Name, c => c.TargetId);

        (await CallResultAsync(staff, ids["Quan Tâm"], "interested")).ShouldBe(HttpStatusCode.NoContent);
        (await CallResultAsync(staff, ids["Không Mua"], "not_interested", "đã mua chỗ khác")).ShouldBe(HttpStatusCode.NoContent);
        (await CallResultAsync(staff, ids["Không Nghe"], "no_answer")).ShouldBe(HttpStatusCode.NoContent);
        (await CallResultAsync(staff, ids["Quan Tâm"], "no_answer")).ShouldBe(HttpStatusCode.Conflict); // không còn trong danh sách
        (await CallResultAsync(staff, ids["Không Nghe"], "maybe")).ShouldBe(HttpStatusCode.BadRequest);

        var targets = (await TargetsAsync(owner)).ToDictionary(t => t.Name!);
        targets["Quan Tâm"].State.ShouldBe("replied");
        targets["Quan Tâm"].LeadStatus.ShouldBe("interested");
        targets["Không Mua"].State.ShouldBe("stopped");
        targets["Không Mua"].LastError.ShouldBe("not_interested");
        targets["Không Mua"].LeadStatus.ShouldBe("lost");
        targets["Không Nghe"].State.ShouldBe("noreply");
        var left = (await CallListAsync(owner)).Single();
        left.Name.ShouldBe("Không Nghe");
        left.CallAttempts.ShouldBe(1);
        left.LastCallAt.ShouldNotBeNull();

        await using var context = db.CreateDbContext(tenantId);
        var notes = await context.ContactNotes.Where(n => n.Kind == ContactNoteKind.Call).ToListAsync();
        notes.Count.ShouldBe(3);
        using var scope = _api.Services.CreateScope();
        var encryptor = scope.ServiceProvider.GetRequiredService<IFieldEncryptor>();
        notes.Select(n => encryptor.Decrypt(n.ContentEnc)).ShouldContain("Gọi điện: Không quan tâm. đã mua chỗ khác");
        var noBuy = await context.Contacts.SingleAsync(c => c.Id == targets["Không Mua"].ContactId);
        noBuy.ProactiveOptOutAt.ShouldNotBeNull(); // không bao giờ nhắn chủ động nữa
        noBuy.ProactiveOptOutSource.ShouldBe("staff_call");
        (await context.AuditLogs.CountAsync(a => a.TenantId == tenantId && a.Action == "care.call_result")).ShouldBe(3);
    }

    [Fact]
    public async Task Another_business_cannot_see_or_record_calls()
    {
        var (tenantA, ownerA, nickA) = await ConnectedTenantAsync("DN A có danh sách gọi");
        await InvitesPerDayAsync(ownerA, nickA, 1);
        await PasteAsync(ownerA, Phone());
        await RunOutreachAsync(tenantA);
        await AgeAsync(tenantA, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromDays(3));
        await MaintenanceAsync(tenantA);
        var targetA = (await CallListAsync(ownerA)).Single().TargetId;

        var (_, ownerB, _) = await ConnectedTenantAsync("DN B tò mò danh sách gọi");
        (await CallListAsync(ownerB)).ShouldBeEmpty();
        (await ownerB.GetFromJsonAsync<ZaloAi.Api.Customers.CareCountResponse>(new Uri("/care/count", UriKind.Relative)))!.ToCall.ShouldBe(0);
        (await CallResultAsync(ownerB, targetA, "not_interested")).ShouldBe(HttpStatusCode.NotFound);
        (await TargetsAsync(ownerA)).Single().State.ShouldBe("noreply");
    }

    [Fact]
    public async Task A_contacted_number_cannot_be_pasted_again_for_90_days_and_at_most_twice()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Dán lại số cũ");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var phone = Phone();
        await PasteAsync(owner, phone);
        await RunOutreachAsync(tenantId);
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromDays(3));
        await MaintenanceAsync(tenantId);

        var again = await PasteDryRunAsync(owner, phone);
        again.Accepted.ShouldBe(0);
        var skip = again.Skipped.Single();
        skip.Reason.ShouldBe("contacted_recently");
        skip.RetryAfter.ShouldNotBeNull().ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(86), DateTimeOffset.UtcNow.AddDays(88));

        // 100 ngày sau: được thử thêm MỘT lần cuối.
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(100), lastMessage: TimeSpan.FromDays(100));
        (await PasteAsync(owner, phone)).Accepted.ShouldBe(1);
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(5));
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(2);

        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(100), lastMessage: TimeSpan.FromDays(100));
        await MaintenanceAsync(tenantId); // lần 2 cũng im → "Cần gọi điện"
        (await PasteDryRunAsync(owner, phone)).Skipped.Single().Reason.ShouldBe("contacted_twice");
        (await CallListAsync(owner)).Single().TargetId.ShouldBe((await TargetsAsync(owner)).OrderByDescending(t => t.FirstMessageAt).First().Id); // chỉ lần dán mới nhất
    }

    [Fact]
    public async Task New_people_wait_for_tomorrow_once_most_of_todays_messages_are_used()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Dành lượt trả lời");
        await InvitesPerDayAsync(owner, connectionId, 1);
        await PasteAsync(owner, Phone());
        var at = WindowTime();
        using (var scope = _api.Services.CreateScope())
        {
            // An toàn: 200 tin/ngày → người mới chỉ được dùng tới 140, phần còn lại dành cho khách đang chat.
            var limiter = ActivatorUtilities.CreateInstance<ZaloAi.Infrastructure.ZaloPersonal.PersonalSendLimiter>(scope.ServiceProvider, new FixedClock(at));
            for (var i = 0; i < 140; i++)
            {
                await limiter.CountAsync(connectionId, ZaloAi.Infrastructure.ZaloPersonal.PacingCounter.Messages, CancellationToken.None);
            }

            var job = ActivatorUtilities.CreateInstance<OutreachTenantJob>(scope.ServiceProvider, new FixedClock(at));
            await job.RunAsync(tenantId, CancellationToken.None);
        }

        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/send");
        var target = (await TargetsAsync(owner)).Single();
        target.State.ShouldBe("queued");
        target.NextActionAt.ShouldNotBeNull().ShouldBeGreaterThan(at.AddHours(1)); // hôm sau
    }

    [Fact]
    public async Task Accepting_the_invite_after_landing_on_the_call_list_still_gets_one_intro()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Chấp nhận muộn");
        var phone = Phone();
        await PasteAsync(owner, phone);
        await RunOutreachAsync(tenantId);
        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(6)); // lời mời gửi sau tin chào
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromDays(3));
        await MaintenanceAsync(tenantId);
        (await TargetsAsync(owner)).Single().State.ShouldBe("noreply");

        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 1L });
        var back = (await TargetsAsync(owner)).Single();
        back.State.ShouldBe("messaged"); // rời danh sách gọi, đếm lại 2 ngày từ tin giới thiệu
        (await CallListAsync(owner)).ShouldBeEmpty();

        await RunOutreachAsync(tenantId, TimeSpan.FromMinutes(30));
        var texts = await ProactiveTextsAsync(tenantId);
        texts.Count.ShouldBe(2); // tin chào + đúng một tin giới thiệu
        texts[1].ShouldContain("cảm ơn");
    }

    [Fact]
    public async Task Call_list_digest_goes_to_telegram_once_a_day_only_when_someone_needs_a_call()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Báo số cần gọi");
        await InvitesPerDayAsync(owner, connectionId, 1);
        await using (var context = db.CreateDbContext(tenantId))
        {
            var settings = await context.HandoffSettings.SingleOrDefaultAsync();
            if (settings is null)
            {
                context.HandoffSettings.Add(new HandoffSettings { TenantId = tenantId, TelegramChatId = "12345" });
            }
            else
            {
                settings.TelegramChatId = "12345";
            }

            await context.SaveChangesAsync();
        }

        var at = WindowTime();
        (await DigestAsync(tenantId, at)).ShouldBeFalse(); // chưa ai cần gọi

        await PasteAsync(owner, Phone());
        await RunOutreachAsync(tenantId);
        await AgeAsync(tenantId, firstMessage: TimeSpan.FromDays(3), lastMessage: TimeSpan.FromDays(3));
        await MaintenanceAsync(tenantId);

        // Job dọn dẹp ở trên (đồng hồ thật) có thể đã báo cho hôm nay → thử ở một ngày khác.
        var later = at.AddDays(2);
        (await DigestAsync(tenantId, later)).ShouldBeTrue();
        (await DigestAsync(tenantId, later.AddMinutes(5))).ShouldBeFalse(); // mỗi ngày một tin

        CallListText.Build(5, 2, "Spa An", "https://app.remi.vn/").ShouldBe(
            "📞 Hôm nay có 5 số cần gọi điện (2 người từng tư vấn rồi im — nên gọi trước) — Spa An\n" +
            "Bot đã nhắn nhưng khách chưa trả lời, hoặc khách không có Zalo. Gọi xong nhớ bấm kết quả.\n" +
            "Mở danh sách: https://app.remi.vn/care#call-list");
    }

    [Fact]
    public async Task Dev_time_travel_ages_only_that_nick_and_the_rules_follow()
    {
        var (tenantId, owner, nick) = await ConnectedTenantAsync("Tua nhanh");
        await InvitesPerDayAsync(owner, nick, 1);
        await PasteAsync(owner, Phone());
        await RunOutreachAsync(tenantId);
        var (tenantB, ownerB, nickB) = await ConnectedTenantAsync("Tua nhanh DN khác");
        await InvitesPerDayAsync(ownerB, nickB, 1);
        await PasteAsync(ownerB, Phone());
        await RunOutreachAsync(tenantB);
        var beforeB = (await TargetsAsync(ownerB)).Single().FirstMessageAt;

        using (var scope = _api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ZaloAi.Infrastructure.Tenancy.TenantContext>().Set(tenantId, userId: null, role: null, isSuperAdmin: false);
            await scope.ServiceProvider.GetRequiredService<ZaloAi.Infrastructure.Repositories.OutreachRepository>()
                .ShiftTimeForDevAsync(tenantId, nick, TimeSpan.FromDays(3), CancellationToken.None);
        }

        await RunOutreachAsync(tenantId); // việc đã hẹn (lời mời sau tin chào) thành tới hạn → chạy (hết lượt mời → hẹn hôm khác)
        await MaintenanceAsync(tenantId);
        (await TargetsAsync(owner)).Single().State.ShouldBe("noreply");
        (await TargetsAsync(ownerB)).Single().FirstMessageAt.ShouldBe(beforeB); // DN khác không bị đụng
        (await TargetsAsync(ownerB)).Single().State.ShouldBe("messaged");
    }

    private async Task<bool> DigestAsync(Guid tenantId, DateTimeOffset at)
    {
        using var scope = _api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ZaloAi.Infrastructure.Tenancy.TenantContext>().Set(tenantId, userId: null, role: null, isSuperAdmin: false);
        var digest = ActivatorUtilities.CreateInstance<CallListDigest>(scope.ServiceProvider, new FixedClock(at));
        return await digest.CheckAsync(tenantId, CancellationToken.None);
    }

    /// <summary>
    /// Lùi mốc thời gian để giả lập ngày trôi qua: tin đầu (FirstMessageAt), tin cuối của hội thoại, tin cuối của khách và lúc khách trả lời.
    /// </summary>
    private async Task AgeAsync(Guid tenantId, TimeSpan firstMessage, TimeSpan lastMessage, TimeSpan? customerMessage = null)
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = db.CreateDbContext(tenantId);
        await context.OutreachTargets.Where(t => t.FirstMessageAt != null).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.FirstMessageAt, now - firstMessage)
            .SetProperty(t => t.NextActionAt, (DateTimeOffset?)null));
        await context.Conversations.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageAt, now - lastMessage));
        if (customerMessage is { } ago)
        {
            await context.Conversations.Where(c => c.LastCustomerMessageAt != null).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastCustomerMessageAt, now - ago));
            await context.OutreachTargets.Where(t => t.RepliedAt != null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RepliedAt, now - ago));
        }
    }

    private static async Task<List<ZaloAi.Api.Customers.CallListItem>> CallListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ZaloAi.Api.Customers.CallListItem>>(new Uri("/care/call-list", UriKind.Relative)))!;

    private static async Task<HttpStatusCode> CallResultAsync(HttpClient client, Guid targetId, string result, string? note = null)
    {
        using var response = await client.PostAsJsonAsync(new Uri($"/care/call-list/{targetId}/result", UriKind.Relative), new { result, note });
        return response.StatusCode;
    }

    private static async Task<OutreachBatchResponse> PasteDryRunAsync(HttpClient client, string phones)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/zalo-outreach/batches", UriKind.Relative), new { phones, origin = "customer", attested = false, dryRun = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OutreachBatchResponse>())!;
    }

    [Fact]
    public async Task Staff_can_paste_but_another_business_sees_and_changes_nothing()
    {
        var (tenantA, ownerA, _) = await ConnectedTenantAsync("DN A dán SĐT");
        var staffA = await _api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantA, ZaloAi.Core.Tenancy.TenantRole.Staff));
        await PasteAsync(staffA, Phone());
        var idsA = (await TargetsAsync(ownerA)).Select(t => t.Id).ToList();
        idsA.Count.ShouldBe(1);

        var (tenantB, ownerB, _) = await ConnectedTenantAsync("DN B tò mò hàng đợi");
        (await TargetsAsync(ownerB)).ShouldBeEmpty();

        using (var stop = await ownerB.PutAsJsonAsync(new Uri("/zalo-outreach/targets", UriKind.Relative), new { ids = idsA, stop = true }))
        {
            stop.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await stop.Content.ReadFromJsonAsync<int>()).ShouldBe(0);
        }

        (await TargetsAsync(ownerA)).Single().State.ShouldBe("queued");

        // Gán nhân viên của DN khác → từ chối.
        var staffB = await db.AddMemberAsync(tenantB, ZaloAi.Core.Tenancy.TenantRole.Staff);
        await using var context = db.CreateDbContext(tenantB);
        var staffBUserId = (await context.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == staffB)).Id;
        using var assign = await ownerA.PutAsJsonAsync(new Uri("/zalo-outreach/targets", UriKind.Relative), new { ids = idsA, assignedUserId = staffBUserId, stop = false });
        assign.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
