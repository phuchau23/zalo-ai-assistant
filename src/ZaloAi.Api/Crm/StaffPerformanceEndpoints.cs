using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Staffing;

namespace ZaloAi.Api.Crm;

/// <param name="Scope">all | service | package | product | item</param>
/// <param name="Kind">percent | fixed</param>
public sealed record CommissionRuleResponse(Guid Id, Guid? StaffMemberId, string Scope, Guid? CatalogItemId, string Kind, long Value, bool IsActive, string? Note)
{
    internal static CommissionRuleResponse From(CommissionRule r) =>
        new(r.Id, r.StaffMemberId, r.Scope.ToString().ToLowerInvariant(), r.CatalogItemId, r.Kind.ToString().ToLowerInvariant(), r.Value, r.IsActive, r.Note);
}

/// <param name="Scope">all | service | package | product | item</param>
/// <param name="Kind">percent | fixed</param>
public sealed record SaveCommissionRuleRequest(Guid? StaffMemberId, string Scope, Guid? CatalogItemId, string Kind, long Value, bool IsActive = true, string? Note = null);

public sealed record CommissionEntryResponse(Guid Id, Guid? InvoiceId, Guid? RedemptionId, long Basis, long Amount, DateTimeOffset At, DateTimeOffset? PaidAt);

public sealed record CommissionStatementResponse(Guid StaffMemberId, string StaffName, string Period, long Total, long Paid, IReadOnlyList<CommissionEntryResponse> Entries);

public sealed record MarkCommissionPaidRequest(Guid StaffMemberId, string Period);

public sealed record AttendanceResponse(Guid Id, Guid StaffMemberId, Guid BranchId, DateTimeOffset ClockInAt, DateTimeOffset? ClockOutAt, string Source, string? Note);

/// <param name="StaffMemberId">Quản lý chấm cho người khác (cần manage_staff); null = chính mình (tài khoản phải gắn nhân viên).</param>
public sealed record ClockRequest(Guid? StaffMemberId, Guid? BranchId, string? Note);

public sealed record EditAttendanceRequest(DateTimeOffset ClockInAt, DateTimeOffset? ClockOutAt, string? Note);

public sealed record StaffPerformanceResponse(Guid StaffMemberId, string StaffName, Guid BranchId, int AppointmentsDone, int NoShows, long Revenue, long Commission, int Customers, int ReturningCustomers, double HoursWorked);

internal sealed class SaveCommissionRuleRequestValidator : AbstractValidator<SaveCommissionRuleRequest>
{
    public SaveCommissionRuleRequestValidator()
    {
        RuleFor(x => x.Scope).Must(s => s is "all" or "service" or "package" or "product" or "item");
        RuleFor(x => x.Kind).Must(k => k is "percent" or "fixed");
        RuleFor(x => x.Value).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

internal sealed class MarkCommissionPaidRequestValidator : AbstractValidator<MarkCommissionPaidRequest>
{
    public MarkCommissionPaidRequestValidator()
    {
        RuleFor(x => x.StaffMemberId).NotEmpty();
        RuleFor(x => x.Period).Matches("^\\d{4}-\\d{2}$").WithMessage("Kỳ dạng yyyy-MM.");
    }
}

/// <summary>Hoa hồng, chấm công, hiệu suất (CRM C5). Nhân viên chỉ thấy số của mình; manage_staff / view_company_revenue thấy mọi người.</summary>
internal static class StaffPerformanceEndpoints
{
    public static IEndpointRouteBuilder MapStaffPerformanceEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/commissions/rules").WithTags("CRM");
        rules.MapGet("", RulesAsync).RequireTenantRole(TenantRole.Staff);
        rules.MapPost("", CreateRuleAsync).RequirePermission(TenantPermissions.ManageStaff).Validate<SaveCommissionRuleRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        rules.MapPut("/{ruleId:guid}", UpdateRuleAsync).RequirePermission(TenantPermissions.ManageStaff).Validate<SaveCommissionRuleRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        rules.MapDelete("/{ruleId:guid}", DeleteRuleAsync).RequirePermission(TenantPermissions.ManageStaff).ProducesProblem(StatusCodes.Status404NotFound);

        var commissions = app.MapGroup("/commissions").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        commissions.MapGet("", StatementsAsync);
        commissions.MapPost("/mark-paid", MarkPaidAsync).RequirePermission(TenantPermissions.ViewCompanyRevenue).Validate<MarkCommissionPaidRequest>();

        var attendance = app.MapGroup("/attendance").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        attendance.MapGet("", ListAttendanceAsync);
        attendance.MapGet("/me", MyAttendanceAsync);
        attendance.MapPost("/clock-in", ClockInAsync).ProducesProblem(StatusCodes.Status409Conflict);
        attendance.MapPost("/clock-out", ClockOutAsync).ProducesProblem(StatusCodes.Status409Conflict);
        attendance.MapPut("/{attendanceId:guid}", EditAttendanceAsync).RequirePermission(TenantPermissions.ManageStaff).ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/staff/performance", PerformanceAsync).WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        return app;
    }

    private static CommissionRuleInput ToInput(SaveCommissionRuleRequest r) => new(
        r.StaffMemberId,
        r.Scope switch { "service" => CommissionScope.Service, "package" => CommissionScope.Package, "product" => CommissionScope.Product, "item" => CommissionScope.Item, _ => CommissionScope.All },
        r.CatalogItemId,
        r.Kind == "fixed" ? CommissionKind.Fixed : CommissionKind.Percent,
        r.Value,
        r.IsActive,
        r.Note);

    private static async Task<Ok<List<CommissionRuleResponse>>> RulesAsync(ITenantContext tenant, CommissionService commissions, CancellationToken cancellationToken) =>
        TypedResults.Ok((await commissions.RulesAsync(tenant.RequireTenantId(), cancellationToken)).Select(CommissionRuleResponse.From).ToList());

    private static async Task<Ok<CommissionRuleResponse>> CreateRuleAsync(SaveCommissionRuleRequest request, ITenantContext tenant, CommissionService commissions, CancellationToken cancellationToken) =>
        TypedResults.Ok(CommissionRuleResponse.From(await commissions.SaveRuleAsync(tenant.RequireTenantId(), null, ToInput(request), cancellationToken)));

    private static async Task<Ok<CommissionRuleResponse>> UpdateRuleAsync(Guid ruleId, SaveCommissionRuleRequest request, ITenantContext tenant, CommissionService commissions, CancellationToken cancellationToken) =>
        TypedResults.Ok(CommissionRuleResponse.From(await commissions.SaveRuleAsync(tenant.RequireTenantId(), ruleId, ToInput(request), cancellationToken)));

    private static async Task<NoContent> DeleteRuleAsync(Guid ruleId, ITenantContext tenant, CommissionService commissions, CancellationToken cancellationToken)
    {
        await commissions.DeleteRuleAsync(tenant.RequireTenantId(), ruleId, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <param name="period">yyyy-MM (mặc định tháng hiện tại giờ VN)</param>
    /// <param name="staffMemberId">Chỉ một người; nhân viên không có quyền xem doanh thu công ty → tự động là chính mình.</param>
    private static async Task<Ok<List<CommissionStatementResponse>>> StatementsAsync(
        string? period, Guid? staffMemberId, ITenantContext tenant, CommissionService commissions, AttendanceService attendance, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var scope = await ScopeAsync(tenantId, tenant, staffMemberId, attendance, cancellationToken);
        var p = period ?? time.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var statements = await commissions.StatementsAsync(tenantId, p, scope, cancellationToken);
        return TypedResults.Ok(statements.Select(s => new CommissionStatementResponse(s.StaffMemberId, s.StaffName, s.Period, s.Total, s.Paid,
            s.Entries.Select(e => new CommissionEntryResponse(e.Id, e.InvoiceId, e.RedemptionId, e.Basis, e.Amount, e.At, e.PaidAt)).ToList())).ToList());
    }

    private static async Task<NoContent> MarkPaidAsync(MarkCommissionPaidRequest request, ITenantContext tenant, CommissionService commissions, CancellationToken cancellationToken)
    {
        await commissions.MarkPaidAsync(tenant.RequireTenantId(), request.StaffMemberId, request.Period, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<AttendanceResponse>>> ListAttendanceAsync(
        Guid? staffMemberId, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to, ITenantContext tenant, AttendanceService attendance, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var scope = await ScopeAsync(tenantId, tenant, staffMemberId, attendance, cancellationToken);
        var now = time.GetUtcNow();
        var list = await attendance.ListAsync(tenantId, scope, branchId, from ?? now.AddDays(-30), to ?? now.AddDays(1), cancellationToken);
        return TypedResults.Ok(list.Select(ToResponse).ToList());
    }

    /// <summary>Ca đang mở của chính mình (tài khoản gắn nhân viên); 204 nếu không có hoặc chưa gắn.</summary>
    private static async Task<Results<Ok<AttendanceResponse>, NoContent>> MyAttendanceAsync(ITenantContext tenant, AttendanceService attendance, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var me = await attendance.MeAsync(tenantId, cancellationToken);
        var open = me is null ? null : await attendance.OpenAsync(tenantId, me.Id, cancellationToken);
        return open is null ? TypedResults.NoContent() : TypedResults.Ok(ToResponse(open));
    }

    private static async Task<Ok<AttendanceResponse>> ClockInAsync(ClockRequest request, ITenantContext tenant, AttendanceService attendance, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var (staffId, byManager) = await TargetAsync(tenantId, tenant, request.StaffMemberId, attendance, cancellationToken);
        return TypedResults.Ok(ToResponse(await attendance.ClockInAsync(tenantId, staffId, request.BranchId, byManager, request.Note, cancellationToken)));
    }

    private static async Task<Ok<AttendanceResponse>> ClockOutAsync(ClockRequest request, ITenantContext tenant, AttendanceService attendance, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var (staffId, _) = await TargetAsync(tenantId, tenant, request.StaffMemberId, attendance, cancellationToken);
        return TypedResults.Ok(ToResponse(await attendance.ClockOutAsync(tenantId, staffId, request.Note, cancellationToken)));
    }

    private static async Task<Ok<AttendanceResponse>> EditAttendanceAsync(Guid attendanceId, EditAttendanceRequest request, ITenantContext tenant, AttendanceService attendance, CancellationToken cancellationToken) =>
        TypedResults.Ok(ToResponse(await attendance.EditAsync(tenant.RequireTenantId(), attendanceId, request.ClockInAt, request.ClockOutAt, request.Note, cancellationToken)));

    /// <param name="from">ISO 8601 (mặc định 30 ngày)</param>
    private static async Task<Ok<List<StaffPerformanceResponse>>> PerformanceAsync(
        DateTimeOffset? from, DateTimeOffset? to, Guid? branchId, Guid? staffMemberId, ITenantContext tenant, PerformanceService performance, AttendanceService attendance, TimeProvider time, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var scope = await ScopeAsync(tenantId, tenant, staffMemberId, attendance, cancellationToken);
        var now = time.GetUtcNow();
        var rows = await performance.ReportAsync(tenantId, from ?? now.AddDays(-30), to ?? now.AddDays(1), branchId, scope, cancellationToken);
        return TypedResults.Ok(rows.Select(r => new StaffPerformanceResponse(r.StaffMemberId, r.StaffName, r.BranchId, r.AppointmentsDone, r.NoShows, r.Revenue, r.Commission, r.Customers, r.ReturningCustomers, r.HoursWorked)).ToList());
    }

    /// <summary>Nhân viên không có quyền xem doanh thu công ty chỉ được xem chính mình (tài khoản phải gắn hồ sơ nhân viên).</summary>
    private static async Task<Guid?> ScopeAsync(Guid tenantId, ITenantContext tenant, Guid? requested, AttendanceService attendance, CancellationToken cancellationToken)
    {
        if (tenant.Can(TenantPermissions.ViewCompanyRevenue) || tenant.Can(TenantPermissions.ManageStaff))
        {
            return requested;
        }

        var me = await attendance.MeAsync(tenantId, cancellationToken) ?? throw new ForbiddenException();
        return requested is { } r && r != me.Id ? throw new ForbiddenException() : me.Id;
    }

    private static async Task<(Guid StaffId, bool ByManager)> TargetAsync(Guid tenantId, ITenantContext tenant, Guid? requested, AttendanceService attendance, CancellationToken cancellationToken)
    {
        var me = await attendance.MeAsync(tenantId, cancellationToken);
        if (requested is { } r && (me is null || r != me.Id))
        {
            return tenant.Can(TenantPermissions.ManageStaff) ? (r, true) : throw new ForbiddenException();
        }

        return me is null ? throw new ConflictException("Tài khoản chưa gắn với hồ sơ nhân viên.", "no_staff_profile") : (me.Id, false);
    }

    private static AttendanceResponse ToResponse(Attendance a) => new(a.Id, a.StaffMemberId, a.BranchId, a.ClockInAt, a.ClockOutAt, a.Source, a.Note);
}
