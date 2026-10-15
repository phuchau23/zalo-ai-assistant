using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Api.Chat;
using ZaloAi.Api.Crm;
using ZaloAi.Api.Knowledge;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.IntegrationTests.Infrastructure;

namespace ZaloAi.IntegrationTests.Api;

/// <summary>CRM C2: chỗ trống, đặt tay / dời / hủy / trạng thái, sức chứa giường, bot tự chốt qua chat, nhắc lịch, cô lập tenant.</summary>
[Collection(PostgresGroup.Name)]
public sealed class CrmAppointmentTests(PostgresFixture db)
{
    private const string Knowledge = """
        { "services": [ { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 450000 } ] }
        """;

    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>Ngày mai (giờ VN) lúc <paramref name="hour"/>:<paramref name="minute"/>.</summary>
    private static DateTimeOffset Tomorrow(int hour, int minute = 0)
    {
        var local = DateTimeOffset.UtcNow.ToOffset(Vietnam).Date.AddDays(1);
        return new DateTimeOffset(local.Year, local.Month, local.Day, hour, minute, 0, Vietnam);
    }

    private static string DateOf(DateTimeOffset at) => at.ToOffset(Vietnam).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task RunJobAsync<TJob>(Func<TJob, Task> run)
        where TJob : notnull
    {
        using var scope = db.Api.Services.CreateScope();
        await run(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    /// <summary>Tenant có dịch vụ 60 phút, chi nhánh mở 24/7, nhân viên Lan ca cả ngày. Trả (tenantId, client, serviceId, branchId, staffId).</summary>
    private async Task<(Guid TenantId, HttpClient Owner, Guid ServiceId, Guid BranchId, Guid StaffId)> SetupAsync(string name)
    {
        var (tenantId, _) = await db.CreateTenantAsync(name);
        var owner = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantId));
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(Knowledge)), "file", "data.json" } };
        using var upload = await owner.PostAsync(Url("/knowledge/imports"), form);
        var preview = (await upload.Content.ReadFromJsonAsync<KnowledgeImportResponse>())!;
        (await owner.PostAsJsonAsync(Url($"/knowledge/imports/{preview.Id}/apply"), new { selections = preview.Items.Select(i => new { key = i.Key }) })).EnsureSuccessStatusCode();
        await RunJobAsync<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));

        var branch = (await owner.GetFromJsonAsync<List<BranchResponse>>(Url("/branches")))!.Single();
        var allDay = Enumerable.Range(0, 7).Select(d => new BranchHourItem(d, "00:00", "23:59")).ToList();
        (await owner.PutAsJsonAsync(Url($"/branches/{branch.Id}/hours"), new SetBranchHoursRequest(allDay))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var service = (await owner.GetFromJsonAsync<List<CatalogItemResponse>>(Url("/catalog?kind=service")))!.Single();

        var staff = await AddStaffAsync(owner, branch.Id, "Lan");
        return (tenantId, owner, service.Id, branch.Id, staff);
    }

    private static async Task<Guid> AddStaffAsync(HttpClient owner, Guid branchId, string name)
    {
        using var created = await owner.PostAsJsonAsync(Url("/staff"), new SaveStaffRequest(branchId, name, null, null, "#2563eb", true, null));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staff = (await created.Content.ReadFromJsonAsync<StaffResponse>())!;
        var shifts = Enumerable.Range(0, 7).Select(d => new ShiftTemplateItem(d, "00:00", "23:59")).ToList();
        (await owner.PutAsJsonAsync(Url($"/staff/{staff.Id}/shift-templates"), new SetShiftTemplatesRequest(shifts))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return staff.Id;
    }

    private static CreateAppointmentRequest Booking(Guid serviceId, DateTimeOffset start, string phone = "0912345678", Guid? staffId = null) =>
        new(null, null, new NewContactRequest("Nguyễn Thị Lan", phone, "chị"), staffId, null, start, [serviceId], "Khách VIP", 1, "staff", false);

    [Fact]
    public async Task Staff_books_reschedules_and_cancels_with_availability_checks()
    {
        var (tenantId, owner, serviceId, branchId, staffId) = await SetupAsync("Lịch tay");
        var start = Tomorrow(15);

        var slots = (await owner.GetFromJsonAsync<List<AvailableSlotResponse>>(Url($"/appointments/availability?branchId={branchId}&date={DateOf(start)}&catalogItemIds={serviceId}")))!;
        slots.ShouldContain(s => s.StartAt == start && s.StaffMemberId == staffId);

        using var created = await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var appointment = (await created.Content.ReadFromJsonAsync<AppointmentResponse>())!;
        (appointment.Status, appointment.Source, appointment.StaffName, appointment.ContactName, appointment.ContactPhone, appointment.Note)
            .ShouldBe(("booked", "staff", "Lan", "Nguyễn Thị Lan", "0912***678", "Khách VIP"));
        appointment.Items.ShouldHaveSingleItem().Name.ShouldBe("Massage Đông y 60 phút");
        appointment.EndAt.ShouldBe(start.AddMinutes(60));

        // Cùng giờ / chồng giờ với nhân viên duy nhất → kín.
        await (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start, "0987654321"))).ShouldBeProblemAsync(409, "slot_unavailable");
        await (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start.AddMinutes(30), "0987654321"))).ShouldBeProblemAsync(409, "slot_unavailable");
        (await owner.GetFromJsonAsync<List<AvailableSlotResponse>>(Url($"/appointments/availability?branchId={branchId}&date={DateOf(start)}&catalogItemIds={serviceId}")))!
            .ShouldNotContain(s => s.StartAt >= start.AddMinutes(-30) && s.StartAt < start.AddMinutes(60));
        // Đặt quá sớm (dưới 60 phút) → too_soon.
        await (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, DateTimeOffset.UtcNow.AddMinutes(10), "0987654321"))).ShouldBeProblemAsync(409, "too_soon");

        // Cùng SĐT → không tạo khách trùng.
        using var second = await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start.AddHours(2)));
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<AppointmentResponse>())!.ContactId.ShouldBe(appointment.ContactId);

        // Dời sang 17:00 → chồng lịch thứ hai → kín; dời 19:00 → được.
        await (await owner.PostAsJsonAsync(Url($"/appointments/{appointment.Id}/reschedule"), new RescheduleAppointmentRequest(start.AddHours(2), null))).ShouldBeProblemAsync(409, "slot_unavailable");
        using var moved = await owner.PostAsJsonAsync(Url($"/appointments/{appointment.Id}/reschedule"), new RescheduleAppointmentRequest(start.AddHours(4), null));
        moved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await moved.Content.ReadFromJsonAsync<AppointmentResponse>())!.StartAt.ShouldBe(start.AddHours(4));

        // Trạng thái: đến → xong; xong rồi không hủy được.
        (await owner.PatchAsJsonAsync(Url($"/appointments/{appointment.Id}/status"), new SetAppointmentStatusRequest("arrived"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.PatchAsJsonAsync(Url($"/appointments/{appointment.Id}/status"), new SetAppointmentStatusRequest("done"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await (await owner.PostAsJsonAsync(Url($"/appointments/{appointment.Id}/cancel"), new CancelAppointmentRequest("thử"))).ShouldBeProblemAsync(409, "appointment_state");
        await (await owner.PatchAsJsonAsync(Url($"/appointments/{appointment.Id}/status"), new SetAppointmentStatusRequest("arrived"))).ShouldBeProblemAsync(409, "appointment_state");

        var detail = (await owner.GetFromJsonAsync<AppointmentDetailResponse>(Url($"/appointments/{appointment.Id}")))!;
        detail.Events.Select(e => e.Kind).ShouldBe(["created", "rescheduled", "status_changed", "status_changed"]);
        detail.ContactVisits.ShouldBe(1);
        detail.ContactLeadStatus.ShouldBe("won");

        var list = (await owner.GetFromJsonAsync<List<AppointmentResponse>>(Url($"/appointments?from={Uri.EscapeDataString(start.AddDays(-1).ToString("o"))}&to={Uri.EscapeDataString(start.AddDays(1).ToString("o"))}")))!;
        list.Count.ShouldBe(2);

        await using var context = db.CreateDbContext(tenantId);
        var stored = await context.Appointments.SingleAsync(a => a.Id == appointment.Id);
        stored.NoteEnc!.ShouldNotContain("Khách VIP");
    }

    [Fact]
    public async Task Beds_cap_concurrent_appointments_and_series_reports_each_occurrence()
    {
        var (_, owner, serviceId, branchId, _) = await SetupAsync("Giường");
        await AddStaffAsync(owner, branchId, "Mai");
        using var bed = await owner.PostAsJsonAsync(Url("/resources"), new SaveResourceRequest(branchId, "Giường 1", "bed"));
        bed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var start = Tomorrow(10);

        (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start, "0987654321"))).ShouldBeProblemAsync(409, "slot_unavailable"); // 2 nhân viên nhưng 1 giường

        (await owner.PostAsJsonAsync(Url("/resources"), new SaveResourceRequest(branchId, "Giường 2", "bed"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, start, "0987654321"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Định kỳ 3 buổi cách 7 ngày lúc 14:00; buổi 2 bị chiếm trước → báo lỗi riêng buổi đó.
        (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, Tomorrow(14).AddDays(7), "0911111111") with { AllowOverlap = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, Tomorrow(14).AddDays(7), "0922222222"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var series = await owner.PostAsJsonAsync(Url("/appointments/series"), new CreateSeriesRequest(Booking(serviceId, Tomorrow(14), "0933333333"), 7, 3));
        series.StatusCode.ShouldBe(HttpStatusCode.OK);
        var outcomes = (await series.Content.ReadFromJsonAsync<List<SeriesOutcomeResponse>>())!;
        outcomes.Count.ShouldBe(3);
        outcomes[0].Appointment.ShouldNotBeNull();
        outcomes[1].Error.ShouldBe("slot_unavailable");
        outcomes[2].Appointment!.SeriesId.ShouldBe(outcomes[0].Appointment!.SeriesId);
    }

    [Fact]
    public async Task Bot_books_through_chat_customer_cancels_and_policy_hands_off()
    {
        var (tenantId, owner, serviceId, branchId, _) = await SetupAsync("Bot đặt lịch");
        using var create = await owner.PostAsync(Url("/chat-test/conversations"), null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;

        var after = await SendAsync(owner, tenantId, conversation.Id, "chị muốn đặt lịch massage đông y 60 phút 15h ngày mai");
        after.Mode.ShouldBe("bot");
        var texts = after.Messages.Where(m => m.Sender == "bot").Select(m => m.Text).ToList();
        texts.ShouldContain(t => t.Contains("em kiểm tra lịch", StringComparison.OrdinalIgnoreCase));
        texts.ShouldContain(t => t.Contains("đã giữ lịch cho chị", StringComparison.Ordinal) && t.Contains("15:00", StringComparison.Ordinal) && t.Contains("Massage Đông y 60 phút", StringComparison.Ordinal));

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(3).ToString("o"));
        var booked = (await owner.GetFromJsonAsync<List<AppointmentResponse>>(Url($"/appointments?from={from}&to={to}")))!.ShouldHaveSingleItem();
        (booked.Source, booked.ConversationId, booked.StartAt, booked.StaffName).ShouldBe(("bot", conversation.Id, Tomorrow(15), "Lan"));

        // Hỏi giờ trống: 15:00 đã kín → danh sách không có 15:00.
        var slots = await SendAsync(owner, tenantId, conversation.Id, "ngày mai còn giờ nào trống massage đông y 60 phút");
        var slotText = slots.Messages.Last(m => m.Sender == "bot").Text;
        slotText.ShouldContain("còn trống");
        slotText.ShouldNotContain("15:00");

        // Khách đặt tiếp đúng 15h → kín → gợi ý giờ khác (hệ thống soạn, không phải AI).
        var clash = await SendAsync(owner, tenantId, conversation.Id, "đặt lịch massage đông y 60 phút 15h ngày mai cho bạn chị nữa");
        clash.Messages.Last(m => m.Sender == "bot").Text.ShouldContain("kín");
        (await owner.GetFromJsonAsync<List<AppointmentResponse>>(Url($"/appointments?from={from}&to={to}")))!.Count.ShouldBe(1);

        // Hủy qua chat → hủy được (trước giờ hẹn > 2 giờ).
        var cancelled = await SendAsync(owner, tenantId, conversation.Id, "chị muốn hủy lịch");
        cancelled.Messages.Last(m => m.Sender == "bot").Text.ShouldContain("đã hủy lịch");
        (await owner.GetFromJsonAsync<AppointmentDetailResponse>(Url($"/appointments/{booked.Id}")))!.Appointment.Status.ShouldBe("cancelled");

        // Đặt lại, rồi siết chính sách hủy 100 giờ → hủy qua chat bị từ chối + chuyển nhân viên.
        await SendAsync(owner, tenantId, conversation.Id, "đặt lịch massage đông y 60 phút 16h ngày mai");
        var settings = (await owner.GetFromJsonAsync<CrmSettingsResponse>(Url("/tenant/crm-settings")))!;
        (await owner.PutAsJsonAsync(Url("/tenant/crm-settings"), new UpdateCrmSettingsRequest(settings.TimeZone, settings.SlotMinutes, settings.DiscountApprovalPercent, settings.DefaultBufferMinutes, CancelLeadHours: 100))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tooLate = await SendAsync(owner, tenantId, conversation.Id, "hủy lịch giúp chị");
        tooLate.Messages.Last(m => m.Sender == "bot" || m.Sender == "system").Text.ShouldNotBeEmpty();
        tooLate.Messages.ShouldContain(m => m.Text.Contains("báo trước 100 giờ", StringComparison.Ordinal));
        tooLate.Mode.ShouldBe("human");
        tooLate.HandoffReason.ShouldBe("booking");

        // Nhóm 3 người → bot không tự chốt, chuyển nhân viên.
        (await owner.PostAsync(Url($"/chat-test/conversations/{conversation.Id}/return-to-bot"), null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var group = await SendAsync(owner, tenantId, conversation.Id, "đặt lịch massage đông y 60 phút 18h ngày mai cho 3 người");
        group.Mode.ShouldBe("human");
        (await owner.GetFromJsonAsync<List<AppointmentResponse>>(Url($"/appointments?from={from}&to={to}&includeCancelled=true")))!.Count.ShouldBe(2);
        _ = serviceId;
        _ = branchId;
    }

    [Fact]
    public async Task Reminder_job_sends_before_appointment_and_customer_confirms_with_ok()
    {
        var (tenantId, owner, serviceId, _, _) = await SetupAsync("Nhắc lịch");
        using var create = await owner.PostAsync(Url("/chat-test/conversations"), null);
        var conversation = (await create.Content.ReadFromJsonAsync<ChatTestConversationResponse>())!;
        await SendAsync(owner, tenantId, conversation.Id, "xin chào bên mình có massage đông y 60 phút không");

        // Lịch sau 90 phút (đặt tay nhưng gắn hội thoại để nhắc qua chat).
        var contactId = await db.CreateDbContext(tenantId).Conversations.Where(c => c.Id == conversation.Id).Select(c => c.ContactId).SingleAsync();
        var raw = DateTimeOffset.UtcNow.AddMinutes(90);
        var start = new DateTimeOffset(raw.Year, raw.Month, raw.Day, raw.Hour, raw.Minute - raw.Minute % 30, 0, TimeSpan.Zero);
        using var created = await owner.PostAsJsonAsync(Url("/appointments"), new CreateAppointmentRequest(null, contactId, null, null, null, start, [serviceId], null));
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var appointment = (await created.Content.ReadFromJsonAsync<AppointmentResponse>())!;

        await RunJobAsync<AppointmentReminderJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        var after = (await owner.GetFromJsonAsync<ChatTestConversationResponse>(Url($"/chat-test/conversations/{conversation.Id}")))!;
        after.Messages[^1].Text.ShouldContain("nhắc lịch");
        var detail = (await owner.GetFromJsonAsync<AppointmentDetailResponse>(Url($"/appointments/{appointment.Id}")))!;
        detail.Appointment.Reminder1SentAt.ShouldNotBeNull(); // 24h và 2h đều tới hạn → cả hai mốc đánh dấu, chỉ gửi một tin
        detail.Appointment.Reminder2SentAt.ShouldNotBeNull();
        detail.Events.Count(e => e.Kind == "reminder_sent").ShouldBe(1);

        // Chạy lại không gửi trùng; khách "ok" → xác nhận, không gọi AI.
        await RunJobAsync<AppointmentReminderJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        var confirmed = await SendAsync(owner, tenantId, conversation.Id, "ok");
        confirmed.Messages[^1].Text.ShouldContain("xác nhận lịch");
        confirmed.AiCalls.ShouldBe(after.AiCalls);
        (await owner.GetFromJsonAsync<AppointmentDetailResponse>(Url($"/appointments/{appointment.Id}")))!.Appointment.Status.ShouldBe("confirmed");
    }

    [Fact]
    public async Task Tenant_B_cannot_see_or_change_tenant_A_appointments()
    {
        var (_, ownerA, serviceId, branchId, _) = await SetupAsync("Lịch A");
        var created = (await (await ownerA.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, Tomorrow(11)))).Content.ReadFromJsonAsync<AppointmentResponse>())!;

        var (tenantB, _) = await db.CreateTenantAsync("Lịch B");
        var ownerB = await db.Api.CreateLoggedInClientAsync(PostgresFixture.OwnerEmail(tenantB));
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(3).ToString("o"));

        (await ownerB.GetFromJsonAsync<List<AppointmentResponse>>(Url($"/appointments?from={from}&to={to}")))!.ShouldBeEmpty();
        (await ownerB.GetAsync(Url($"/appointments/{created.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url($"/appointments/{created.Id}/cancel"), new CancelAppointmentRequest(null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PatchAsJsonAsync(Url($"/appointments/{created.Id}/status"), new SetAppointmentStatusRequest("done"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.GetAsync(Url($"/appointments/availability?branchId={branchId}&date={DateOf(Tomorrow(11))}&catalogItemIds={serviceId}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.PostAsJsonAsync(Url("/appointments"), Booking(serviceId, Tomorrow(11)) with { BranchId = branchId })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ownerA.GetFromJsonAsync<AppointmentDetailResponse>(Url($"/appointments/{created.Id}")))!.Appointment.Status.ShouldBe("booked");
    }

    private async Task<ChatTestConversationResponse> SendAsync(HttpClient client, Guid tenantId, Guid conversationId, string text)
    {
        using var send = await client.PostAsJsonAsync(Url($"/chat-test/conversations/{conversationId}/messages"), new { text });
        send.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var message = (await send.Content.ReadFromJsonAsync<ChatMessageResponse>())!;
        await RunJobAsync<ProcessIncomingMessageJob>(job => job.RunAsync(tenantId, message.Id, CancellationToken.None));
        return (await client.GetFromJsonAsync<ChatTestConversationResponse>(Url($"/chat-test/conversations/{conversationId}")))!;
    }
}
