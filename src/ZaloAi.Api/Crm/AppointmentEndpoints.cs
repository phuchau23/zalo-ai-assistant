using FluentValidation;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;

namespace ZaloAi.Api.Crm;

public sealed record AppointmentItemResponse(Guid CatalogItemId, string Name, int DurationMinutes, long? Price);

/// <param name="Status">booked | confirmed | arrived | in_progress | done | no_show | cancelled</param>
/// <param name="Source">bot | staff | online | walk_in</param>
/// <param name="StartAt">UTC (ISO 8601); FE đổi sang giờ địa phương theo CrmSettings.timeZone.</param>
public sealed record AppointmentResponse(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid ContactId,
    string ContactName,
    string? ContactPhone,
    string? ContactSalutation,
    Guid? StaffMemberId,
    string? StaffName,
    Guid? ResourceId,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int BufferMinutes,
    string Status,
    string Source,
    int PartySize,
    Guid? ConversationId,
    Guid? SeriesId,
    string? Note,
    string? AiBrief,
    DateTimeOffset? AiBriefAt,
    DateTimeOffset? Reminder1SentAt,
    DateTimeOffset? Reminder2SentAt,
    DateTimeOffset? CustomerConfirmedAt,
    string? CancelReason,
    IReadOnlyList<AppointmentItemResponse> Items)
{
    internal static AppointmentResponse From(AppointmentView v) => new(
        v.Appointment.Id, v.Appointment.BranchId, v.BranchName, v.Appointment.ContactId, v.ContactName, v.ContactPhone, v.ContactSalutation,
        v.Appointment.StaffMemberId, v.StaffName, v.Appointment.ResourceId, v.Appointment.StartAt, v.Appointment.EndAt, v.Appointment.BufferMinutes,
        Snake(v.Appointment.Status), Snake(v.Appointment.Source), v.Appointment.PartySize, v.Appointment.ConversationId, v.Appointment.SeriesId,
        v.Note, v.AiBrief, v.Appointment.AiBriefAt, v.Appointment.Reminder1SentAt, v.Appointment.Reminder2SentAt, v.Appointment.CustomerConfirmedAt,
        v.Appointment.CancelReason, v.Items.Select(i => new AppointmentItemResponse(i.CatalogItemId, i.Name, i.DurationMinutes, i.PriceSnapshot)).ToList());

    internal static string Snake(Enum value) => string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <param name="Actor">staff | bot | customer | system</param>
public sealed record AppointmentEventResponse(string Kind, string Actor, Guid? ByUserId, string? Data, DateTimeOffset At);

public sealed record AppointmentDetailResponse(AppointmentResponse Appointment, IReadOnlyList<AppointmentEventResponse> Events, string? ContactLeadStatus, int ContactVisits, int ContactNoShows);

public sealed record NewContactRequest(string Name, string? Phone, string? Salutation);

/// <param name="ContactId">Khách có sẵn; hoặc <paramref name="NewContact"/> để tạo khách mới.</param>
/// <param name="StartAt">ISO 8601 có múi giờ (FE gửi giờ địa phương kèm offset).</param>
/// <param name="Source">staff | walk_in (mặc định staff)</param>
/// <param name="AllowOverlap">Xếp chồng cố ý (chủ / quyền manage_staff).</param>
public sealed record CreateAppointmentRequest(
    Guid? BranchId,
    Guid? ContactId,
    NewContactRequest? NewContact,
    Guid? StaffMemberId,
    Guid? ResourceId,
    DateTimeOffset StartAt,
    IReadOnlyList<Guid> CatalogItemIds,
    string? Note,
    int PartySize = 1,
    string Source = "staff",
    bool AllowOverlap = false);

/// <param name="IntervalDays">Cách nhau bao nhiêu ngày (7 = hằng tuần).</param>
public sealed record CreateSeriesRequest(CreateAppointmentRequest First, int IntervalDays, int Occurrences);

public sealed record SeriesOutcomeResponse(DateTimeOffset StartAt, AppointmentResponse? Appointment, string? Error);

public sealed record RescheduleAppointmentRequest(DateTimeOffset StartAt, Guid? StaffMemberId);

/// <param name="Status">confirmed | arrived | in_progress | done | no_show</param>
public sealed record SetAppointmentStatusRequest(string Status);

public sealed record CancelAppointmentRequest(string? Reason);

public sealed record AssignAppointmentRequest(Guid? StaffMemberId, Guid? ResourceId);

public sealed record UpdateAppointmentNoteRequest(string? Note);

public sealed record AvailableSlotResponse(Guid? StaffMemberId, string? StaffName, DateTimeOffset StartAt, DateTimeOffset EndAt);

/// <param name="Kind">bed | room | device</param>
public sealed record ResourceResponse(Guid Id, Guid BranchId, string Name, string Kind, bool IsActive)
{
    internal static ResourceResponse From(Resource r) => new(r.Id, r.BranchId, r.Name, r.Kind.ToString().ToLowerInvariant(), r.IsActive);
}

/// <param name="Kind">bed | room | device</param>
public sealed record SaveResourceRequest(Guid BranchId, string Name, string Kind, bool IsActive = true);

/// <param name="Status">waiting | offered | booked | cancelled</param>
public sealed record WaitlistResponse(Guid Id, Guid BranchId, Guid ContactId, string ContactName, Guid CatalogItemId, string ServiceName, Guid? StaffMemberId, DateTimeOffset PreferredFrom, DateTimeOffset PreferredTo, string Status, DateTimeOffset? OfferedSlotAt);

public sealed record AddWaitlistRequest(Guid BranchId, Guid ContactId, Guid CatalogItemId, Guid? StaffMemberId, DateTimeOffset PreferredFrom, DateTimeOffset PreferredTo, string? Note);

/// <param name="Status">booked | cancelled</param>
public sealed record SetWaitlistStatusRequest(string Status);

internal sealed class CreateAppointmentRequestValidator : AbstractValidator<CreateAppointmentRequest>
{
    public CreateAppointmentRequestValidator()
    {
        RuleFor(x => x.CatalogItemIds).NotNull().Must(c => c.Count is >= 1 and <= 10).WithMessage("Chọn 1–10 dịch vụ.");
        RuleFor(x => x.PartySize).InclusiveBetween(1, 50);
        RuleFor(x => x.Note).MaximumLength(2000);
        RuleFor(x => x.Source).Must(s => s is "staff" or "walk_in").WithMessage("Nguồn staff hoặc walk_in.");
        RuleFor(x => x).Must(x => x.ContactId is not null || x.NewContact is not null).WithMessage("Cần chọn khách hoặc nhập khách mới.");
        When(x => x.NewContact is not null, () =>
        {
            RuleFor(x => x.NewContact!.Name).NotEmpty().MaximumLength(200).WithName("Tên khách");
            RuleFor(x => x.NewContact!.Phone).MaximumLength(30);
        });
    }
}

internal sealed class CreateSeriesRequestValidator : AbstractValidator<CreateSeriesRequest>
{
    public CreateSeriesRequestValidator()
    {
        RuleFor(x => x.First).NotNull().SetValidator(new CreateAppointmentRequestValidator());
        RuleFor(x => x.IntervalDays).InclusiveBetween(1, 90);
        RuleFor(x => x.Occurrences).InclusiveBetween(2, 52);
    }
}

internal sealed class RescheduleAppointmentRequestValidator : AbstractValidator<RescheduleAppointmentRequest>
{
    public RescheduleAppointmentRequestValidator() => RuleFor(x => x.StartAt).NotEqual(default(DateTimeOffset)).WithMessage("Thiếu giờ mới.");
}

internal sealed class SetAppointmentStatusRequestValidator : AbstractValidator<SetAppointmentStatusRequest>
{
    public SetAppointmentStatusRequestValidator() =>
        RuleFor(x => x.Status).Must(s => AppointmentEndpoints.ParseStatus(s) is not null).WithMessage("Trạng thái: confirmed, arrived, in_progress, done, no_show.");
}

internal sealed class CancelAppointmentRequestValidator : AbstractValidator<CancelAppointmentRequest>
{
    public CancelAppointmentRequestValidator() => RuleFor(x => x.Reason).MaximumLength(500);
}

internal sealed class UpdateAppointmentNoteRequestValidator : AbstractValidator<UpdateAppointmentNoteRequest>
{
    public UpdateAppointmentNoteRequestValidator() => RuleFor(x => x.Note).MaximumLength(2000);
}

internal sealed class SaveResourceRequestValidator : AbstractValidator<SaveResourceRequest>
{
    public SaveResourceRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100).WithName("Tên");
        RuleFor(x => x.Kind).Must(k => k is "bed" or "room" or "device").WithMessage("Loại bed, room hoặc device.");
    }
}

internal sealed class AddWaitlistRequestValidator : AbstractValidator<AddWaitlistRequest>
{
    public AddWaitlistRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.ContactId).NotEmpty();
        RuleFor(x => x.CatalogItemId).NotEmpty();
        RuleFor(x => x.PreferredTo).GreaterThan(x => x.PreferredFrom);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class SetWaitlistStatusRequestValidator : AbstractValidator<SetWaitlistStatusRequest>
{
    public SetWaitlistStatusRequestValidator() => RuleFor(x => x.Status).Must(s => s is "booked" or "cancelled");
}

/// <summary>Lịch hẹn (CRM C2). Mọi thành viên xem/đặt/đổi trạng thái lịch của chi nhánh mình; xếp chồng cố ý cần quyền manage_staff.</summary>
internal static class AppointmentEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/appointments").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        group.MapGet("", ListAsync);
        group.MapGet("/availability", AvailabilityAsync).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{appointmentId:guid}", GetAsync).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("", CreateAsync).Validate<CreateAppointmentRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/series", CreateSeriesAsync).Validate<CreateSeriesRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{appointmentId:guid}/reschedule", RescheduleAsync).Validate<RescheduleAppointmentRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{appointmentId:guid}/cancel", CancelAsync).Validate<CancelAppointmentRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPatch("/{appointmentId:guid}/status", SetStatusAsync).Validate<SetAppointmentStatusRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPatch("/{appointmentId:guid}/assign", AssignAsync).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/{appointmentId:guid}/note", UpdateNoteAsync).Validate<UpdateAppointmentNoteRequest>().ProducesProblem(StatusCodes.Status404NotFound);

        var resources = app.MapGroup("/resources").WithTags("CRM");
        resources.MapGet("", ListResourcesAsync).RequireTenantRole(TenantRole.Staff);
        resources.MapPost("", CreateResourceAsync).RequirePermission(TenantPermissions.ManageBranches).Validate<SaveResourceRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        resources.MapPut("/{resourceId:guid}", UpdateResourceAsync).RequirePermission(TenantPermissions.ManageBranches).Validate<SaveResourceRequest>().ProducesProblem(StatusCodes.Status404NotFound);

        var waitlist = app.MapGroup("/waitlist").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        waitlist.MapGet("", ListWaitlistAsync);
        waitlist.MapPost("", AddWaitlistAsync).Validate<AddWaitlistRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        waitlist.MapPatch("/{entryId:guid}/status", SetWaitlistStatusAsync).Validate<SetWaitlistStatusRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    internal static AppointmentStatus? ParseStatus(string? status) => status switch
    {
        "confirmed" => AppointmentStatus.Confirmed,
        "arrived" => AppointmentStatus.Arrived,
        "in_progress" => AppointmentStatus.InProgress,
        "done" => AppointmentStatus.Done,
        "no_show" => AppointmentStatus.NoShow,
        _ => null,
    };

    /// <param name="from">ISO 8601 (bắt buộc)</param>
    /// <param name="to">ISO 8601 (bắt buộc, tối đa 62 ngày sau from)</param>
    private static async Task<Results<Ok<List<AppointmentResponse>>, ProblemHttpResult>> ListAsync(
        DateTimeOffset? from, DateTimeOffset? to, Guid? branchId, Guid? staffMemberId, Guid? contactId, bool? includeCancelled,
        ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        if (from is not { } f || to is not { } t || t <= f || (t - f) > TimeSpan.FromDays(62))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Khoảng thời gian không hợp lệ (tối đa 62 ngày).",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_input" });
        }

        var list = await appointments.ListAsync(tenant.RequireTenantId(), branchId, f, t, staffMemberId, contactId, includeCancelled == true, cancellationToken);
        return TypedResults.Ok(list.Select(AppointmentResponse.From).ToList());
    }

    /// <param name="date">yyyy-MM-dd (giờ địa phương DN)</param>
    /// <param name="catalogItemIds">Danh sách id dịch vụ, cách nhau dấu phẩy.</param>
    private static async Task<Results<Ok<List<AvailableSlotResponse>>, ProblemHttpResult>> AvailabilityAsync(
        Guid? branchId, string date, string catalogItemIds, Guid? staffMemberId, Guid? ignoreAppointmentId,
        ITenantContext tenant, AvailabilityService availability, BranchService branches, CrmSettingsRepository settingsRepository, CancellationToken cancellationToken)
    {
        var ids = (catalogItemIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        if (!CrmParsing.IsDate(date) || ids.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Cần ngày yyyy-MM-dd và ít nhất một dịch vụ.",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_input" });
        }

        var tenantId = tenant.RequireTenantId();
        var branch = branchId is { } b ? (await branches.GetAsync(tenantId, b, cancellationToken)).Branch : await branches.EnsureDefaultAsync(tenantId, cancellationToken);
        var settings = await settingsRepository.GetAsync(tenantId, cancellationToken);
        var requirement = await availability.RequirementAsync(tenantId, ids, settings, cancellationToken);
        var slots = await availability.SlotsAsync(tenantId, branch.Id, CrmParsing.Date(date), requirement, staffMemberId, ignoreAppointmentId, cancellationToken);
        return TypedResults.Ok(slots.Select(s => new AvailableSlotResponse(s.StaffMemberId, s.StaffName, s.StartAt, s.EndAt)).ToList());
    }

    private static async Task<Ok<AppointmentDetailResponse>> GetAsync(Guid appointmentId, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        var d = await appointments.GetAsync(tenant.RequireTenantId(), appointmentId, cancellationToken);
        return TypedResults.Ok(new AppointmentDetailResponse(
            AppointmentResponse.From(d.View),
            d.Events.Select(e => new AppointmentEventResponse(e.Kind, e.Actor, e.ByUserId, e.DataJson, e.At)).ToList(),
            d.ContactLeadStatus, d.ContactVisits, d.ContactNoShows));
    }

    private static async Task<Ok<AppointmentResponse>> CreateAsync(CreateAppointmentRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var created = await appointments.CreateAsync(tenantId, ToInput(request, tenant), Actor.Staff(tenant.UserId), cancellationToken);
        return TypedResults.Ok(AppointmentResponse.From((await appointments.ViewsAsync(tenantId, [created], includeBrief: false, cancellationToken))[0]));
    }

    private static async Task<Ok<List<SeriesOutcomeResponse>>> CreateSeriesAsync(CreateSeriesRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var outcomes = await appointments.CreateSeriesAsync(tenantId, ToInput(request.First, tenant), request.IntervalDays, request.Occurrences, Actor.Staff(tenant.UserId), cancellationToken);
        var created = outcomes.Where(o => o.Appointment is not null).Select(o => o.Appointment!).ToList();
        var views = (await appointments.ViewsAsync(tenantId, created, includeBrief: false, cancellationToken)).ToDictionary(v => v.Appointment.Id);
        return TypedResults.Ok(outcomes.Select(o => new SeriesOutcomeResponse(o.StartAt, o.Appointment is null ? null : AppointmentResponse.From(views[o.Appointment.Id]), o.Error)).ToList());
    }

    private static async Task<Ok<AppointmentResponse>> RescheduleAsync(Guid appointmentId, RescheduleAppointmentRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var updated = await appointments.RescheduleAsync(tenantId, appointmentId, request.StartAt, request.StaffMemberId, Actor.Staff(tenant.UserId), cancellationToken);
        return TypedResults.Ok(AppointmentResponse.From((await appointments.ViewsAsync(tenantId, [updated], includeBrief: false, cancellationToken))[0]));
    }

    private static async Task<NoContent> CancelAsync(Guid appointmentId, CancelAppointmentRequest request, ITenantContext tenant, AppointmentService appointments, Hangfire.IBackgroundJobClient jobs, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var cancelled = await appointments.CancelAsync(tenantId, appointmentId, request.Reason, Actor.Staff(tenant.UserId), cancellationToken);
        var (branchId, start, end) = (cancelled.BranchId, cancelled.StartAt, cancelled.EndAt);
        jobs.Enqueue<ZaloAi.Infrastructure.Jobs.WaitlistMatchJob>(job => job.RunAsync(tenantId, branchId, start, end, CancellationToken.None));
        return TypedResults.NoContent();
    }

    /// <summary>Hoàn thành mà chưa có hóa đơn → tự trừ buổi gói cho dịch vụ gói còn bao (C3); phần còn lại lễ tân tạo hóa đơn.</summary>
    private static async Task<NoContent> SetStatusAsync(
        Guid appointmentId, SetAppointmentStatusRequest request, ITenantContext tenant, AppointmentService appointments,
        ZaloAi.Infrastructure.Billing.PackageService packages, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var status = ParseStatus(request.Status)!.Value;
        var appointment = await appointments.SetStatusAsync(tenantId, appointmentId, status, Actor.Staff(tenant.UserId), cancellationToken);
        if (status == AppointmentStatus.Done)
        {
            await packages.AutoRedeemForAppointmentAsync(tenantId, appointment, AppointmentResponse.Snake(appointment.Source), cancellationToken);
            await appointments.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<NoContent> AssignAsync(Guid appointmentId, AssignAppointmentRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        await appointments.AssignAsync(tenant.RequireTenantId(), appointmentId, request.StaffMemberId, request.ResourceId, Actor.Staff(tenant.UserId), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UpdateNoteAsync(Guid appointmentId, UpdateAppointmentNoteRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        await appointments.UpdateNoteAsync(tenant.RequireTenantId(), appointmentId, request.Note, Actor.Staff(tenant.UserId), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<ResourceResponse>>> ListResourcesAsync(Guid? branchId, bool? includeInactive, ITenantContext tenant, ResourceService resources, CancellationToken cancellationToken) =>
        TypedResults.Ok((await resources.ListAsync(tenant.RequireTenantId(), branchId, includeInactive == true, cancellationToken)).Select(ResourceResponse.From).ToList());

    private static async Task<Ok<ResourceResponse>> CreateResourceAsync(SaveResourceRequest request, ITenantContext tenant, ResourceService resources, CancellationToken cancellationToken) =>
        TypedResults.Ok(ResourceResponse.From(await resources.CreateAsync(tenant.RequireTenantId(), new ResourceInput(request.BranchId, request.Name, ParseKind(request.Kind)), cancellationToken)));

    private static async Task<Ok<ResourceResponse>> UpdateResourceAsync(Guid resourceId, SaveResourceRequest request, ITenantContext tenant, ResourceService resources, CancellationToken cancellationToken) =>
        TypedResults.Ok(ResourceResponse.From(await resources.UpdateAsync(tenant.RequireTenantId(), resourceId, new ResourceInput(request.BranchId, request.Name, ParseKind(request.Kind)), request.IsActive, cancellationToken)));

    private static async Task<Ok<List<WaitlistResponse>>> ListWaitlistAsync(Guid? branchId, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        var list = await appointments.ListWaitlistAsync(tenant.RequireTenantId(), branchId, cancellationToken);
        return TypedResults.Ok(list.Select(x => new WaitlistResponse(x.Entry.Id, x.Entry.BranchId, x.Entry.ContactId, x.ContactName, x.Entry.CatalogItemId, x.ServiceName, x.Entry.StaffMemberId,
            x.Entry.PreferredFrom, x.Entry.PreferredTo, x.Entry.Status.ToString().ToLowerInvariant(), x.Entry.OfferedSlotAt)).ToList());
    }

    private static async Task<Ok<WaitlistResponse>> AddWaitlistAsync(AddWaitlistRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var entry = await appointments.AddWaitlistAsync(tenantId, request.BranchId, request.ContactId, request.CatalogItemId, request.StaffMemberId, request.PreferredFrom, request.PreferredTo, request.Note, cancellationToken);
        var row = (await appointments.ListWaitlistAsync(tenantId, request.BranchId, cancellationToken)).First(x => x.Entry.Id == entry.Id);
        return TypedResults.Ok(new WaitlistResponse(entry.Id, entry.BranchId, entry.ContactId, row.ContactName, entry.CatalogItemId, row.ServiceName, entry.StaffMemberId, entry.PreferredFrom, entry.PreferredTo, "waiting", null));
    }

    private static async Task<NoContent> SetWaitlistStatusAsync(Guid entryId, SetWaitlistStatusRequest request, ITenantContext tenant, AppointmentService appointments, CancellationToken cancellationToken)
    {
        await appointments.SetWaitlistStatusAsync(tenant.RequireTenantId(), entryId, request.Status == "booked" ? WaitlistStatus.Booked : WaitlistStatus.Cancelled, cancellationToken);
        return TypedResults.NoContent();
    }

    private static ResourceKind ParseKind(string kind) => kind switch { "room" => ResourceKind.Room, "device" => ResourceKind.Device, _ => ResourceKind.Bed };

    private static AppointmentInput ToInput(CreateAppointmentRequest r, ITenantContext tenant) => new(
        r.BranchId,
        r.ContactId,
        r.NewContact is null ? null : new NewContactInput(r.NewContact.Name, r.NewContact.Phone, r.NewContact.Salutation),
        r.StaffMemberId,
        r.ResourceId,
        r.StartAt,
        r.CatalogItemIds,
        r.Note,
        r.PartySize,
        r.Source == "walk_in" ? AppointmentSource.WalkIn : AppointmentSource.Staff,
        null,
        r.AllowOverlap && tenant.Can(TenantPermissions.ManageStaff));
}
