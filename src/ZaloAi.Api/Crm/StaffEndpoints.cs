using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;

namespace ZaloAi.Api.Crm;

/// <param name="Status">active | inactive</param>
public sealed record StaffResponse(Guid Id, Guid BranchId, string DisplayName, string? Title, string Color, bool Bookable, string Status, Guid? UserId)
{
    internal static StaffResponse From(StaffMember s) =>
        new(s.Id, s.BranchId, s.DisplayName, s.Title, s.Color, s.Bookable, s.Status.ToString().ToLowerInvariant(), s.UserId);
}

/// <param name="DayOfWeek">0 = Chủ nhật … 6 = Thứ bảy.</param>
public sealed record ShiftTemplateItem(int DayOfWeek, string StartTime, string EndTime);

public sealed record TimeOffItem(Guid Id, DateTimeOffset FromAt, DateTimeOffset ToAt, string? Reason);

/// <param name="ServiceIds">Rỗng = làm được mọi dịch vụ.</param>
public sealed record StaffDetailResponse(
    StaffResponse Staff,
    string? Phone,
    IReadOnlyList<Guid> ServiceIds,
    IReadOnlyList<ShiftTemplateItem> ShiftTemplates,
    IReadOnlyList<TimeOffItem> TimeOff);

/// <param name="Color">"#RRGGBB"</param>
/// <param name="UserId">Tài khoản đăng nhập gắn với nhân viên này (tùy chọn).</param>
public sealed record SaveStaffRequest(Guid BranchId, string DisplayName, string? Title, string? Phone, string Color, bool Bookable, Guid? UserId);

/// <param name="Status">active | inactive</param>
public sealed record SetStaffStatusRequest(string Status);

public sealed record SetStaffServicesRequest(IReadOnlyList<Guid> ServiceIds);

public sealed record SetShiftTemplatesRequest(IReadOnlyList<ShiftTemplateItem> Templates);

/// <param name="Date">yyyy-MM-dd</param>
/// <param name="Shifts">Rỗng = nghỉ ngày đó.</param>
public sealed record SetDayShiftsRequest(string Date, IReadOnlyList<DayShiftItem> Shifts);

public sealed record DayShiftItem(Guid? BranchId, string StartTime, string EndTime);

/// <param name="Date">yyyy-MM-dd</param>
public sealed record StaffShiftResponse(Guid Id, Guid StaffMemberId, Guid BranchId, string Date, string StartTime, string EndTime, string Source);

public sealed record AddTimeOffRequest(DateTimeOffset FromAt, DateTimeOffset ToAt, string? Reason);

internal sealed class SaveStaffRequestValidator : AbstractValidator<SaveStaffRequest>
{
    public SaveStaffRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200).WithName("Tên");
        RuleFor(x => x.Title).MaximumLength(100);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Color).Matches("^#[0-9a-fA-F]{6}$").WithMessage("Màu dạng #RRGGBB.");
    }
}

internal sealed class SetStaffStatusRequestValidator : AbstractValidator<SetStaffStatusRequest>
{
    public SetStaffStatusRequestValidator() => RuleFor(x => x.Status).Must(s => s is "active" or "inactive").WithMessage("Trạng thái active hoặc inactive.");
}

internal sealed class SetStaffServicesRequestValidator : AbstractValidator<SetStaffServicesRequest>
{
    public SetStaffServicesRequestValidator() => RuleFor(x => x.ServiceIds).NotNull().Must(s => s.Count <= 500);
}

internal sealed class SetShiftTemplatesRequestValidator : AbstractValidator<SetShiftTemplatesRequest>
{
    public SetShiftTemplatesRequestValidator()
    {
        RuleFor(x => x.Templates).NotNull().Must(t => t.Count <= 21);
        RuleForEach(x => x.Templates).ChildRules(t =>
        {
            t.RuleFor(x => x.DayOfWeek).InclusiveBetween(0, 6);
            t.RuleFor(x => x.StartTime).Must(CrmParsing.IsTime).WithMessage("Giờ dạng HH:mm.");
            t.RuleFor(x => x.EndTime).Must(CrmParsing.IsTime).WithMessage("Giờ dạng HH:mm.");
        });
    }
}

internal sealed class SetDayShiftsRequestValidator : AbstractValidator<SetDayShiftsRequest>
{
    public SetDayShiftsRequestValidator()
    {
        RuleFor(x => x.Date).Must(CrmParsing.IsDate).WithMessage("Ngày dạng yyyy-MM-dd.");
        RuleFor(x => x.Shifts).NotNull().Must(s => s.Count <= 3);
        RuleForEach(x => x.Shifts).ChildRules(s =>
        {
            s.RuleFor(x => x.StartTime).Must(CrmParsing.IsTime).WithMessage("Giờ dạng HH:mm.");
            s.RuleFor(x => x.EndTime).Must(CrmParsing.IsTime).WithMessage("Giờ dạng HH:mm.");
        });
    }
}

internal sealed class AddTimeOffRequestValidator : AbstractValidator<AddTimeOffRequest>
{
    public AddTimeOffRequestValidator()
    {
        RuleFor(x => x.ToAt).GreaterThan(x => x.FromAt).WithMessage("Kết thúc phải sau bắt đầu.");
        RuleFor(x => x.Reason).MaximumLength(200);
    }
}

/// <summary>Nhân viên / kỹ thuật viên (CRM C1). Xem: mọi thành viên (giới hạn chi nhánh). Sửa: chủ hoặc quyền manage_staff.</summary>
internal static class StaffEndpoints
{
    public static IEndpointRouteBuilder MapStaffEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/staff").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        read.MapGet("", ListAsync);
        read.MapGet("/{staffId:guid}", GetAsync).ProducesProblem(StatusCodes.Status404NotFound);
        read.MapGet("/shifts", ListShiftsAsync);

        var manage = app.MapGroup("/staff").WithTags("CRM").RequirePermission(TenantPermissions.ManageStaff);
        manage.MapPost("", CreateAsync).Validate<SaveStaffRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPut("/{staffId:guid}", UpdateAsync).Validate<SaveStaffRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPatch("/{staffId:guid}/status", SetStatusAsync).Validate<SetStaffStatusRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPut("/{staffId:guid}/services", SetServicesAsync).Validate<SetStaffServicesRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPut("/{staffId:guid}/shift-templates", SetShiftTemplatesAsync).Validate<SetShiftTemplatesRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPut("/{staffId:guid}/shifts", SetDayShiftsAsync).Validate<SetDayShiftsRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/{staffId:guid}/time-off", AddTimeOffAsync).Validate<AddTimeOffRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapDelete("/{staffId:guid}/time-off/{timeOffId:guid}", RemoveTimeOffAsync).ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<List<StaffResponse>>> ListAsync(Guid? branchId, bool? includeInactive, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        var list = await staff.ListAsync(tenant.RequireTenantId(), branchId, includeInactive == true, cancellationToken);
        return TypedResults.Ok(list.Select(StaffResponse.From).ToList());
    }

    private static async Task<Ok<StaffDetailResponse>> GetAsync(Guid staffId, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        var d = await staff.GetAsync(tenant.RequireTenantId(), staffId, cancellationToken);
        return TypedResults.Ok(new StaffDetailResponse(
            StaffResponse.From(d.Staff),
            d.Phone,
            d.ServiceIds,
            d.ShiftTemplates.Select(t => new ShiftTemplateItem((int)t.DayOfWeek, CrmParsing.Format(t.StartTime), CrmParsing.Format(t.EndTime))).ToList(),
            d.TimeOff.Select(t => new TimeOffItem(t.Id, t.FromAt, t.ToAt, t.Reason)).ToList()));
    }

    /// <param name="from">yyyy-MM-dd</param>
    /// <param name="to">yyyy-MM-dd (tối đa 62 ngày)</param>
    private static async Task<Results<Ok<List<StaffShiftResponse>>, ProblemHttpResult>> ListShiftsAsync(
        string from, string to, Guid? branchId, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        if (!CrmParsing.IsDate(from) || !CrmParsing.IsDate(to) || CrmParsing.Date(to).DayNumber - CrmParsing.Date(from).DayNumber is < 0 or > 62)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Khoảng ngày không hợp lệ (tối đa 62 ngày).",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_input" });
        }

        var list = await staff.ListShiftsAsync(tenant.RequireTenantId(), branchId, CrmParsing.Date(from), CrmParsing.Date(to), cancellationToken);
        return TypedResults.Ok(list.Select(s => new StaffShiftResponse(s.Id, s.StaffMemberId, s.BranchId, CrmParsing.Format(s.Date), CrmParsing.Format(s.StartTime), CrmParsing.Format(s.EndTime), s.Source.ToString().ToLowerInvariant())).ToList());
    }

    private static async Task<Ok<StaffResponse>> CreateAsync(SaveStaffRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken) =>
        TypedResults.Ok(StaffResponse.From(await staff.CreateAsync(tenant.RequireTenantId(), ToInput(request), cancellationToken)));

    private static async Task<Ok<StaffResponse>> UpdateAsync(Guid staffId, SaveStaffRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken) =>
        TypedResults.Ok(StaffResponse.From(await staff.UpdateAsync(tenant.RequireTenantId(), staffId, ToInput(request), cancellationToken)));

    private static async Task<NoContent> SetStatusAsync(Guid staffId, SetStaffStatusRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        await staff.SetStatusAsync(tenant.RequireTenantId(), staffId, request.Status == "active" ? StaffStatus.Active : StaffStatus.Inactive, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetServicesAsync(Guid staffId, SetStaffServicesRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        await staff.SetServicesAsync(tenant.RequireTenantId(), staffId, request.ServiceIds, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetShiftTemplatesAsync(Guid staffId, SetShiftTemplatesRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        var templates = request.Templates.Select(t => new ShiftTemplateInput((DayOfWeek)t.DayOfWeek, CrmParsing.Time(t.StartTime), CrmParsing.Time(t.EndTime))).ToList();
        await staff.SetShiftTemplatesAsync(tenant.RequireTenantId(), staffId, templates, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetDayShiftsAsync(Guid staffId, SetDayShiftsRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        var date = CrmParsing.Date(request.Date);
        var shifts = request.Shifts.Select(s => new ShiftInput(date, s.BranchId, CrmParsing.Time(s.StartTime), CrmParsing.Time(s.EndTime))).ToList();
        await staff.SetShiftsForDateAsync(tenant.RequireTenantId(), staffId, date, shifts, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<TimeOffItem>> AddTimeOffAsync(Guid staffId, AddTimeOffRequest request, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        var t = await staff.AddTimeOffAsync(tenant.RequireTenantId(), staffId, new TimeOffInput(request.FromAt, request.ToAt, request.Reason), cancellationToken);
        return TypedResults.Ok(new TimeOffItem(t.Id, t.FromAt, t.ToAt, t.Reason));
    }

    private static async Task<NoContent> RemoveTimeOffAsync(Guid staffId, Guid timeOffId, ITenantContext tenant, StaffService staff, CancellationToken cancellationToken)
    {
        await staff.RemoveTimeOffAsync(tenant.RequireTenantId(), staffId, timeOffId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static StaffInput ToInput(SaveStaffRequest r) => new(r.BranchId, r.DisplayName, r.Title, r.Phone, r.Color, r.Bookable, r.UserId);
}
