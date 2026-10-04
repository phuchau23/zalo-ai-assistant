using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Customers;
using ZaloAi.Api.Inbox;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>Khách hàng (tầng tiềm năng, nhãn, ghi chú, xuất Excel), "Cần chăm sóc" và kết nối Telegram tự phục vụ (M6, AI giả).</summary>
[Collection(PostgresGroup.Name)]
public sealed class CustomerCareTests(PostgresFixture db)
{
    private static readonly string[] NewTags = ["VIP", " vip ", "khách cũ"];

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task RunJobsAsync(Guid tenantId)
    {
        await using var context = db.CreateDbContext(tenantId);
        var customer = await context.Messages.Where(m => m.Sender == MessageSender.Customer).OrderBy(m => m.CreatedAt).Select(m => m.Id).ToListAsync();
        foreach (var id in customer)
        {
            using var scope = db.Api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProcessIncomingMessageJob>().RunAsync(tenantId, id, CancellationToken.None);
        }
    }

    /// <summary>Khách chat thử nhắn 1 tin (AI giả không có dữ liệu → chuyển người), rồi trả lại bot để không còn "chờ người".</summary>
    private async Task<(Guid TenantId, HttpClient Owner, Guid ConversationId, Guid ContactId)> CustomerAsync(
        string name, bool autoSend = false, string text = "gói triệt lông bao nhiêu tiền")
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        await SetAutoSendAsync(owner, autoSend);
        using var create = await owner.PostAsync(Url("/chat-test/conversations"), null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;
        await owner.PostAsJsonAsync(Url($"/chat-test/conversations/{conversation.Id}/messages"), new { text });
        await RunJobsAsync(tenantId);
        (await owner.PostAsync(Url($"/inbox/conversations/{conversation.Id}/return-to-bot"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detail = (await owner.GetFromJsonAsync<InboxConversationDetail>(Url($"/inbox/conversations/{conversation.Id}")))!;
        return (tenantId, owner, conversation.Id, detail.Conversation.ContactId);
    }

    /// <summary>Bật/tắt "bot tự nhắn" (tắt = chỉ gợi ý, để test các luồng nhân viên).</summary>
    private static async Task SetAutoSendAsync(HttpClient owner, bool autoSend)
    {
        var s = (await owner.GetFromJsonAsync<HandoffSettingsResponse>(Url("/tenant/handoff-settings")))!;
        var update = new UpdateHandoffSettingsRequest(
            s.HandoffMessage, s.AfterHoursMessage, s.TakeoverMessageEnabled, s.TakeoverMessage, s.ReturnToBotMessageEnabled, s.ReturnToBotMessage,
            s.StaffSignatureEnabled, s.ResponseTime, s.OpenTime, s.CloseTime, s.WorkingDays, s.ReminderMinutes, s.TelegramChatId,
            s.CareEnabled, s.CareColdHours, autoSend, "07:00", "21:00");
        (await owner.PutAsJsonAsync(Url("/tenant/handoff-settings"), update)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Chạy các job "bot tự nhắn" đã tới giờ (test không có Hangfire server).</summary>
    private async Task RunProactiveAsync(Guid tenantId, TimeProvider? clock = null)
    {
        await using var context = db.CreateDbContext(tenantId);
        var due = await context.CareSuggestions.Where(s => s.Status == CareSuggestionStatus.Open && s.ScheduledSendAt != null).Select(s => s.Id).ToListAsync();
        foreach (var id in due)
        {
            using var scope = db.Api.Services.CreateScope();
            var job = clock is null
                ? scope.ServiceProvider.GetRequiredService<ProactiveSendJob>()
                : ActivatorUtilities.CreateInstance<ProactiveSendJob>(scope.ServiceProvider, clock);
            await job.RunAsync(tenantId, id, CancellationToken.None);
        }
    }

    private async Task AnalyzeAsync(Guid tenantId, Guid conversationId, TimeProvider? clock = null)
    {
        using var scope = db.Api.Services.CreateScope();
        var job = clock is null
            ? scope.ServiceProvider.GetRequiredService<CareAnalysisJob>()
            : ActivatorUtilities.CreateInstance<CareAnalysisJob>(scope.ServiceProvider, clock);
        await job.AnalyzeConversationAsync(tenantId, conversationId, CancellationToken.None);
    }

    [Fact]
    public async Task Contacts_list_status_tags_and_notes()
    {
        var (_, owner, _, contactId) = await CustomerAsync("Khách hàng cơ bản");

        var list = (await owner.GetFromJsonAsync<ContactListResponse>(Url("/contacts")))!;
        var item = list.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(contactId);
        item.IsTest.ShouldBeTrue();
        item.LeadStatus.ShouldBe("new");
        item.LastCustomerMessageAt.ShouldNotBeNull();
        list.Counts["new"].ShouldBe(1);
        (await owner.GetFromJsonAsync<ContactListResponse>(Url("/contacts?includeTest=false")))!.Items.ShouldBeEmpty();

        using var patched = await owner.PatchAsJsonAsync(Url($"/contacts/{contactId}"), new { leadStatus = "won", tags = NewTags });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await patched.Content.ReadFromJsonAsync<ContactListItem>())!;
        updated.LeadStatus.ShouldBe("won");
        updated.LeadStatusManual.ShouldBeTrue();
        updated.Tags.ShouldBe(["VIP", "khách cũ"]);
        (await owner.GetFromJsonAsync<ContactListResponse>(Url("/contacts?status=won&tag=VIP")))!.Items.ShouldHaveSingleItem();
        (await owner.GetFromJsonAsync<ContactListResponse>(Url("/contacts?status=hot")))!.Items.ShouldBeEmpty();
        (await owner.PatchAsJsonAsync(Url($"/contacts/{contactId}"), new { leadStatus = "vip" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var note = await owner.PostAsJsonAsync(
            Url($"/contacts/{contactId}/notes"),
            new { kind = "service", text = "Đã làm massage 90 phút, hẹn tái khám", happenedOn = "2026-10-05", followUpAt = "2026-10-12T09:30:00+07:00" });
        note.StatusCode.ShouldBe(HttpStatusCode.OK);
        var created = (await note.Content.ReadFromJsonAsync<ContactNoteItem>())!;
        created.AuthorName.ShouldNotBeNullOrEmpty();
        created.FollowUpAt.ShouldBe(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(7)));
        (await owner.PostAsJsonAsync(Url($"/contacts/{contactId}/notes"), new { kind = "note", text = "x", happenedOn = "2026-10-05", followUpAt = "2026-10-04T23:00:00+07:00" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var detail = (await owner.GetFromJsonAsync<ContactDetail>(Url($"/contacts/{contactId}")))!;
        detail.Notes.ShouldHaveSingleItem().Text.ShouldBe("Đã làm massage 90 phút, hẹn tái khám");
        detail.Conversations.ShouldHaveSingleItem();

        (await owner.DeleteAsync(Url($"/contacts/{contactId}/notes/{created.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<ContactDetail>(Url($"/contacts/{contactId}")))!.Notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Staff_cannot_export_or_delete_others_notes()
    {
        var (tenantId, owner, _, contactId) = await CustomerAsync("Phân quyền khách hàng");
        var staff = await db.Api.CreateLoggedInClientAsync(await db.AddMemberAsync(tenantId, TenantRole.Staff));

        using var note = await owner.PostAsJsonAsync(Url($"/contacts/{contactId}/notes"), new { kind = "note", text = "ghi chú của chủ", happenedOn = "2026-10-05" });
        var ownerNote = (await note.Content.ReadFromJsonAsync<ContactNoteItem>())!;
        (await staff.DeleteAsync(Url($"/contacts/{contactId}/notes/{ownerNote.Id}"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.GetAsync(Url("/contacts/export"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var export = await owner.GetAsync(Url("/contacts/export?includeTest=true"));
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        (await export.Content.ReadAsByteArrayAsync()).Length.ShouldBeGreaterThan(1000);

        await using var context = db.CreateDbContext(tenantId);
        (await context.AuditLogs.AnyAsync(a => a.Action == "contacts.exported")).ShouldBeTrue();
    }

    [Fact]
    public async Task Care_analysis_creates_suggestion_then_customer_reply_closes_it()
    {
        var (tenantId, owner, conversationId, contactId) = await CustomerAsync("Cần chăm sóc");
        await AnalyzeAsync(tenantId, conversationId);
        await AnalyzeAsync(tenantId, conversationId); // phân tích lại → cập nhật, không tạo gợi ý thứ 2

        var open = (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!;
        var suggestion = open.ShouldHaveSingleItem();
        suggestion.ContactId.ShouldBe(contactId);
        suggestion.Temperature.ShouldBe("hot");
        suggestion.Draft.ShouldNotBeNullOrEmpty();
        suggestion.MessagingDeadline.ShouldBeNull(); // chat thử: không giới hạn
        suggestion.LeadStatus.ShouldBe("hot"); // AI đánh giá nóng → tự nâng tầng
        (await owner.GetFromJsonAsync<CareCountResponse>(Url("/care/count")))!.Open.ShouldBe(1);

        // Khách nhắn lại → gợi ý tự xong.
        await owner.PostAsJsonAsync(Url($"/chat-test/conversations/{conversationId}/messages"), new { text = "dạ cho chị đặt lịch thứ 7" });
        await RunJobsAsync(tenantId);
        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldBeEmpty();
        var done = (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?status=done")))!.ShouldHaveSingleItem();
        done.Outcome.ShouldBe("customer_replied");
    }

    [Fact]
    public async Task Staff_resolves_suggestion_and_owner_assigns()
    {
        var (tenantId, owner, conversationId, _) = await CustomerAsync("Xử lý gợi ý");
        await AnalyzeAsync(tenantId, conversationId);
        var suggestion = (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.Single();

        var staffEmail = await db.AddMemberAsync(tenantId, TenantRole.Staff);
        var staff = await db.Api.CreateLoggedInClientAsync(staffEmail);
        var members = (await owner.GetFromJsonAsync<List<TenantMemberResponse>>(Url("/tenant/members")))!;
        var staffId = members.Single(m => m.Role == "staff").UserId;

        (await staff.PostAsJsonAsync(Url($"/care/suggestions/{suggestion.Id}/assign"), new { userId = staffId })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsJsonAsync(Url($"/care/suggestions/{suggestion.Id}/assign"), new { userId = staffId })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var mine = (await staff.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?filter=mine")))!.ShouldHaveSingleItem();
        mine.AssignedName.ShouldNotBeNullOrEmpty();

        (await staff.PatchAsJsonAsync(Url($"/care/suggestions/{suggestion.Id}"), new { status = "done", outcome = "booked" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await staff.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?status=done")))!.ShouldHaveSingleItem().Outcome.ShouldBe("booked");
        (await staff.PatchAsJsonAsync(Url($"/care/suggestions/{suggestion.Id}"), new { status = "expired" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Bấm "AI phân tích" thủ công: lần 2 trong 2 phút bị chặn (mỗi lần là một lần gọi AI có phí).
        (await staff.PostAsync(Url($"/care/conversations/{conversationId}/analyze"), null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await staff.PostAsync(Url($"/care/conversations/{conversationId}/analyze"), null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Follow_up_note_due_creates_follow_up_suggestion_and_sweep_expires_overdue()
    {
        var (tenantId, owner, _, contactId) = await CustomerAsync("Hẹn chăm sóc lại");
        using var note = await owner.PostAsJsonAsync(
            Url($"/contacts/{contactId}/notes"),
            new { kind = "appointment", text = "Hẹn tái khám", happenedOn = "2026-10-01", followUpAt = "2026-10-02T14:15:00+07:00" });
        var noteId = (await note.Content.ReadFromJsonAsync<ContactNoteItem>())!.Id;

        using (var scope = db.Api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CareAnalysisJob>().FollowUpAsync(tenantId, noteId, CancellationToken.None);
        }

        var suggestion = (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldHaveSingleItem();
        suggestion.Trigger.ShouldBe("follow_up");

        await using (var context = db.CreateDbContext(tenantId))
        {
            (await context.ContactNotes.SingleAsync(n => n.Id == noteId)).FollowUpQueuedAt.ShouldNotBeNull();
            var row = await context.CareSuggestions.SingleAsync(s => s.Id == suggestion.Id);
            row.MessagingDeadline = DateTimeOffset.UtcNow.AddMinutes(-1); // giả lập quá hạn nhắn qua Zalo
            await context.SaveChangesAsync();
        }

        using (var scope = db.Api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CareFollowUpSweepJob>().RunAsync(CancellationToken.None);
        }

        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?status=expired")))!.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Tenant_B_cannot_see_or_change_tenant_A_customers_or_suggestions()
    {
        var (tenantA, _, conversationId, contactId) = await CustomerAsync("Khách A");
        await AnalyzeAsync(tenantA, conversationId);
        Guid suggestionId;
        Guid noteId;
        await using (var context = db.CreateDbContext(tenantA))
        {
            suggestionId = (await context.CareSuggestions.SingleAsync()).Id;
            var note = new ContactNote { TenantId = tenantA, ContactId = contactId, ContentEnc = "x", HappenedOn = new DateOnly(2026, 10, 1) };
            context.ContactNotes.Add(note);
            await context.SaveChangesAsync();
            noteId = note.Id;
        }

        var (tenantB, _) = await db.CreateTenantAsync("Khách B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));

        (await ownerB.GetFromJsonAsync<ContactListResponse>(Url("/contacts")))!.Items.ShouldBeEmpty();
        (await ownerB.GetAsync(Url($"/contacts/{contactId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PatchAsJsonAsync(Url($"/contacts/{contactId}"), new { leadStatus = "lost" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url($"/contacts/{contactId}/notes"), new { kind = "note", text = "x", happenedOn = "2026-10-05" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.DeleteAsync(Url($"/contacts/{contactId}/notes/{noteId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldBeEmpty();
        (await ownerB.PatchAsJsonAsync(Url($"/care/suggestions/{suggestionId}"), new { status = "skipped" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsync(Url($"/care/conversations/{conversationId}/analyze"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Lớp 2: repository từ chối tenant khác dù gọi trực tiếp.
        await using var contextB = db.CreateDbContext(tenantB);
        (await contextB.Contacts.AnyAsync(c => c.Id == contactId)).ShouldBeFalse();
        (await contextB.ContactNotes.AnyAsync()).ShouldBeFalse();
        (await contextB.CareSuggestions.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Telegram_link_code_connects_group_once()
    {
        var (tenantId, owner, _, _) = await CustomerAsync("Kết nối Telegram");

        // Môi trường test không cài bot → API báo rõ, không tạo mã.
        await (await owner.PostAsync(Url("/tenant/handoff-settings/telegram/link-code"), null)).ShouldBeProblemAsync(409, "telegram_not_configured");

        string code;
        using (var scope = db.Api.Services.CreateScope())
        {
            code = await scope.ServiceProvider.GetRequiredService<TelegramLinkService>().CreateCodeAsync(tenantId, null, CancellationToken.None);
        }

        using (var scope = db.Api.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<TelegramLinkService>().HandleCommandAsync("-1009876543210", code, CancellationToken.None)).ShouldBeTrue();
        }

        using (var scope = db.Api.Services.CreateScope())
        {
            // Mã dùng 1 lần.
            (await scope.ServiceProvider.GetRequiredService<TelegramLinkService>().HandleCommandAsync("-100111", code, CancellationToken.None)).ShouldBeFalse();
        }

        var settings = (await owner.GetFromJsonAsync<HandoffSettingsResponse>(Url("/tenant/handoff-settings")))!;
        settings.TelegramChatId.ShouldBe("-1009876543210");
        settings.CareEnabled.ShouldBeTrue();
        settings.CareColdHours.ShouldBe(6);

        (await owner.DeleteAsync(Url("/tenant/handoff-settings/telegram"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<HandoffSettingsResponse>(Url("/tenant/handoff-settings")))!.TelegramChatId.ShouldBeNull();
    }

    /// <summary>Đồng hồ cố định cho job (test không phụ thuộc giờ chạy).</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Thời điểm gần nhất (không sau hiện tại) nằm trong 08:00–20:00 giờ Việt Nam.</summary>
    private static FixedClock InWindowClock()
    {
        var t = DateTimeOffset.UtcNow;
        while (!CareDecision.InSendWindow(new HandoffSettings { CareSendStart = "08:00", CareSendEnd = "20:00" }, t))
        {
            t = t.AddMinutes(-30);
        }

        return new FixedClock(t);
    }

    private static FixedClock NightClock()
    {
        var vn = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        return new FixedClock(new DateTimeOffset(vn.Year, vn.Month, vn.Day, 23, 0, 0, TimeSpan.FromHours(7)).AddDays(-1).ToUniversalTime());
    }

    [Fact]
    public async Task Bot_sends_one_proactive_message_then_waits_for_customer_reply()
    {
        var (tenantId, owner, conversationId, contactId) = await CustomerAsync("Bot tự chăm sóc", autoSend: true);

        // 23:00 giờ VN: bot không nhắn đêm, hẹn sáng hôm sau 07:00 (khung DN cài 07:00–21:00).
        var night = NightClock();
        await AnalyzeAsync(tenantId, conversationId, night);
        var scheduled = (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldHaveSingleItem();
        scheduled.EscalationReason.ShouldBeNull();
        scheduled.ScheduledSendAt!.Value.ToOffset(TimeSpan.FromHours(7)).Hour.ShouldBe(7);
        await RunProactiveAsync(tenantId, night);
        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?status=autosent")))!.ShouldBeEmpty();

        // Trong giờ: bot tự nhắn ngay.
        var day = InWindowClock();
        await AnalyzeAsync(tenantId, conversationId, day);
        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldHaveSingleItem().ScheduledSendAt!.Value.ShouldBe(day.GetUtcNow(), TimeSpan.FromMilliseconds(1)); // Postgres lưu tới micro giây
        await RunProactiveAsync(tenantId, day);

        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?status=autosent")))!.ShouldHaveSingleItem();
        var detail = (await owner.GetFromJsonAsync<InboxConversationDetail>(Url($"/inbox/conversations/{conversationId}")))!;
        var last = detail.Messages[^1];
        last.Sender.ShouldBe("bot");
        last.Proactive.ShouldBeTrue();
        last.DeliveryStatus.ShouldBe("sent");
        detail.Conversation.Mode.ShouldBe("bot"); // khách trả lời thì bot nói chuyện tiếp

        // Khách chưa trả lời → phân tích lại cũng không nhắn tin thứ hai.
        await AnalyzeAsync(tenantId, conversationId, day);
        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldBeEmpty();

        // Khách trả lời → hết "đang chờ trả lời", bot trả lời như thường.
        await owner.PostAsJsonAsync(Url($"/chat-test/conversations/{conversationId}/messages"), new { text = "dạ chị vẫn quan tâm" });
        await RunJobsAsync(tenantId);
        await using var context = db.CreateDbContext(tenantId);
        var contact = await context.Contacts.SingleAsync(c => c.Id == contactId);
        contact.ProactiveAwaitingReply.ShouldBeFalse();
        contact.LastProactiveAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Sensitive_case_calls_staff_instead_of_bot_sending()
    {
        var (tenantId, owner, conversationId, _) = await CustomerAsync("Gọi nhân viên", autoSend: true, text: "em muốn phàn nàn về buổi hôm qua");
        await AnalyzeAsync(tenantId, conversationId);

        var suggestion = (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldHaveSingleItem();
        suggestion.EscalationReason.ShouldBe("sensitive");
        suggestion.ScheduledSendAt.ShouldBeNull();
        await RunProactiveAsync(tenantId);
        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions?status=autosent")))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Customer_opt_out_stops_proactive_messages_and_staff_cannot_override()
    {
        var (tenantId, owner, conversationId, contactId) = await CustomerAsync("Khách hủy nhận tin", autoSend: true);
        await owner.PostAsJsonAsync(Url($"/chat-test/conversations/{conversationId}/messages"), new { text = "Hủy" });
        await RunJobsAsync(tenantId);

        var detail = (await owner.GetFromJsonAsync<InboxConversationDetail>(Url($"/inbox/conversations/{conversationId}")))!;
        detail.Messages[^1].Text.ShouldBe(ProcessIncomingMessageJob.OptOutReply);
        var contact = (await owner.GetFromJsonAsync<ContactDetail>(Url($"/contacts/{contactId}")))!.Contact;
        contact.ProactiveOptOut.ShouldBe("customer");
        await (await owner.PatchAsJsonAsync(Url($"/contacts/{contactId}"), new { proactiveEnabled = true })).ShouldBeProblemAsync(409, "customer_opted_out");

        await AnalyzeAsync(tenantId, conversationId);
        (await owner.GetFromJsonAsync<List<CareSuggestionItem>>(Url("/care/suggestions")))!.ShouldBeEmpty();

        // Nhân viên tự tắt rồi bật lại được cho khách khác.
        var (_, owner2, _, contact2) = await CustomerAsync("Nhân viên tắt nhắn", autoSend: true);
        (await owner2.PatchAsJsonAsync(Url($"/contacts/{contact2}"), new { proactiveEnabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner2.GetFromJsonAsync<ContactDetail>(Url($"/contacts/{contact2}")))!.Contact.ProactiveOptOut.ShouldBe("staff");
        (await owner2.PatchAsJsonAsync(Url($"/contacts/{contact2}"), new { proactiveEnabled = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
