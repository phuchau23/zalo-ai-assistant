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
        // Mốc = lúc sớm nhất trong khung giờ được nhắn (chạy test ban đêm → sáng mai), rồi cộng "after": lượt sau luôn muộn hơn lượt trước.
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

        using var scope = _api.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<OutreachTenantJob>(scope.ServiceProvider, new FixedClock(t));
        await job.RunAsync(tenantId, CancellationToken.None);
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
    /// Đặt số lời mời/ngày của nick. 1 = người mới KHÔNG được mời (lượt duy nhất giữ cho người đã trả lời) → luồng "nhắn trước";
    /// mặc định (nick mới tuần đầu: 3, người mới dùng 2) → luồng "kết bạn trước" (chủ dự án chốt 2026-11-15).
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
    public async Task Invite_first_then_greeting_after_accepting()
    {
        // Mặc định (nick mới tuần đầu 3 lời mời/ngày, người mới dùng 2): còn lượt mời → KẾT BẠN TRƯỚC, chưa nhắn gì (2026-11-15).
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Hành trình kết bạn");
        var phone = Phone();
        await PasteAsync(owner, $"{phone}, chị Hoa");

        await RunOutreachAsync(tenantId);

        var invite = _sidecar.Calls.Single(c => c.Path == $"/accounts/{connectionId}/friend-request");
        var inviteBody = JsonDocument.Parse(invite.Body).RootElement;
        inviteBody.GetProperty("uid").GetString().ShouldBe($"u-{phone}");
        var inviteText = inviteBody.GetProperty("message").GetString()!;
        inviteText.Length.ShouldBeLessThanOrEqualTo(150);
        inviteText.ShouldContain("trợ lý AI");
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/send");
        (await TargetsAsync(owner)).Single().State.ShouldBe("invited");

        await RunOutreachAsync(tenantId); // không mời lần hai, không nhắn thêm khi khách chưa chấp nhận
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);
        _sidecar.Calls.ShouldNotContain(c => c.Path == $"/accounts/{connectionId}/send");

        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 1L });
        (await TargetsAsync(owner)).Single().State.ShouldBe("friend");

        await RunOutreachAsync(tenantId);
        (await TargetsAsync(owner)).Single().State.ShouldBe("messaged");
        var greeting = (await ProactiveTextsAsync(tenantId)).ShouldHaveSingleItem();
        greeting.ShouldContain("trợ lý AI");
        greeting.Any(char.IsDigit).ShouldBeFalse();
    }

    [Fact]
    public async Task New_people_use_the_invites_left_for_them_then_get_messaged()
    {
        // 2 lời mời/ngày: 1 giữ cho người đã trả lời, 1 cho người mới → người thứ nhất được mời, người thứ hai được nhắn trước.
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Hết lượt mời thì nhắn");
        await InvitesPerDayAsync(owner, connectionId, 2);
        await PasteAsync(owner, $"{Phone()}\n{Phone()}");

        await RunOutreachAsync(tenantId);

        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/friend-request").ShouldBe(1);
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(1);
        (await TargetsAsync(owner)).Select(t => t.State).OrderBy(s => s, StringComparer.Ordinal).ShouldBe(["invited", "messaged"]);
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
        await RunOutreachAsync(tenantId); // kết bạn trước
        await EventsAsync(connectionId, new { type = "friend", @event = "add", uid = $"u-{phone}", ts = 1L });

        await RunOutreachAsync(tenantId);
        await RunOutreachAsync(tenantId);

        (await TargetsAsync(owner)).Single().State.ShouldBe("failed");
        // Đúng 1 lần thử nhắn sau khi kết bạn; không gửi lại ở các lượt sau.
        _sidecar.Calls.Count(c => c.Path == $"/accounts/{connectionId}/send").ShouldBe(1);
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

    [Fact]
    public async Task Silence_for_a_week_moves_to_the_call_list_and_opt_out_declines()
    {
        var (tenantId, owner, connectionId) = await ConnectedTenantAsync("Dọn hàng đợi");
        await InvitesPerDayAsync(owner, connectionId, 1);
        var quiet = Phone();
        var optOut = Phone();
        await PasteAsync(owner, $"{quiet}\n{optOut}");
        await RunOutreachAsync(tenantId);

        await using (var context = db.CreateDbContext(tenantId))
        {
            foreach (var t in await context.OutreachTargets.ToListAsync())
            {
                t.FirstMessageAt = DateTimeOffset.UtcNow.AddDays(-8);
            }

            (await context.Contacts.SingleAsync(c => c.ExternalUserId == $"u-{optOut}")).ProactiveOptOutAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        await MaintenanceAsync(tenantId);

        var targets = await TargetsAsync(owner);
        targets.Select(t => t.State).OrderBy(s => s, StringComparer.Ordinal).ShouldBe(["declined", "noreply"]);
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
