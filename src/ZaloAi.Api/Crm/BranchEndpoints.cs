using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;

namespace ZaloAi.Api.Crm;

/// <param name="Status">active | inactive</param>
public sealed record BranchResponse(Guid Id, string Code, string Name, string? Address, string? Phone, bool IsDefault, string Status)
{
    internal static BranchResponse From(Branch b) =>
        new(b.Id, b.Code, b.Name, b.Address, b.Phone, b.IsDefault, b.Status.ToString().ToLowerInvariant());
}

/// <param name="DayOfWeek">0 = Chủ nhật … 6 = Thứ bảy.</param>
/// <param name="OpenTime">"HH:mm"</param>
/// <param name="CloseTime">"HH:mm"</param>
public sealed record BranchHourItem(int DayOfWeek, string OpenTime, string CloseTime);

/// <param name="Date">yyyy-MM-dd</param>
public sealed record BranchClosureItem(Guid Id, string Date, string? Reason);

public sealed record BranchDetailResponse(BranchResponse Branch, IReadOnlyList<BranchHourItem> Hours, IReadOnlyList<BranchClosureItem> Closures);

public sealed record SaveBranchRequest(string Code, string Name, string? Address, string? Phone);

public sealed record SetBranchHoursRequest(IReadOnlyList<BranchHourItem> Hours);

/// <param name="Date">yyyy-MM-dd</param>
public sealed record AddBranchClosureRequest(string Date, string? Reason);

/// <param name="Status">active | inactive</param>
public sealed record SetBranchStatusRequest(string Status);

internal sealed class SaveBranchRequestValidator : AbstractValidator<SaveBranchRequest>
{
    public SaveBranchRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50).WithName("Mã");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithName("Tên chi nhánh");
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.Phone).MaximumLength(30);
    }
}

internal sealed class SetBranchHoursRequestValidator : AbstractValidator<SetBranchHoursRequest>
{
    public SetBranchHoursRequestValidator()
    {
        RuleFor(x => x.Hours).NotNull().Must(h => h.Count <= 21).WithMessage("Tối đa 3 khung giờ mỗi ngày.");
        RuleForEach(x => x.Hours).ChildRules(h =>
        {
            h.RuleFor(x => x.DayOfWeek).InclusiveBetween(0, 6);
            h.RuleFor(x => x.OpenTime).Must(CrmParsing.IsTime).WithMessage("Giờ dạng HH:mm.");
            h.RuleFor(x => x.CloseTime).Must(CrmParsing.IsTime).WithMessage("Giờ dạng HH:mm.");
        });
    }
}

internal sealed class AddBranchClosureRequestValidator : AbstractValidator<AddBranchClosureRequest>
{
    public AddBranchClosureRequestValidator()
    {
        RuleFor(x => x.Date).Must(CrmParsing.IsDate).WithMessage("Ngày dạng yyyy-MM-dd.");
        RuleFor(x => x.Reason).MaximumLength(200);
    }
}

internal sealed class SetBranchStatusRequestValidator : AbstractValidator<SetBranchStatusRequest>
{
    public SetBranchStatusRequestValidator() => RuleFor(x => x.Status).Must(s => s is "active" or "inactive").WithMessage("Trạng thái active hoặc inactive.");
}

/// <summary>Chi nhánh (CRM C1). Xem: mọi thành viên (nhân viên chỉ thấy chi nhánh mình). Sửa: chủ hoặc quyền manage_branches.</summary>
internal static class BranchEndpoints
{
    public static IEndpointRouteBuilder MapBranchEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/branches").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        read.MapGet("", ListAsync);
        read.MapGet("/{branchId:guid}", GetAsync).ProducesProblem(StatusCodes.Status404NotFound);

        var manage = app.MapGroup("/branches").WithTags("CRM").RequirePermission(TenantPermissions.ManageBranches);
        manage.MapPost("", CreateAsync).Validate<SaveBranchRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPut("/{branchId:guid}", UpdateAsync).Validate<SaveBranchRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPost("/{branchId:guid}/default", SetDefaultAsync).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPatch("/{branchId:guid}/status", SetStatusAsync).Validate<SetBranchStatusRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPut("/{branchId:guid}/hours", SetHoursAsync).Validate<SetBranchHoursRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/{branchId:guid}/closures", AddClosureAsync).Validate<AddBranchClosureRequest>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapDelete("/{branchId:guid}/closures/{closureId:guid}", RemoveClosureAsync).ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<List<BranchResponse>>> ListAsync(bool? includeInactive, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        var list = await branches.ListAsync(tenant.RequireTenantId(), includeInactive == true, cancellationToken);
        return TypedResults.Ok(list.Select(BranchResponse.From).ToList());
    }

    private static async Task<Ok<BranchDetailResponse>> GetAsync(Guid branchId, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        var detail = await branches.GetAsync(tenant.RequireTenantId(), branchId, cancellationToken);
        return TypedResults.Ok(new BranchDetailResponse(
            BranchResponse.From(detail.Branch),
            detail.Hours.Select(h => new BranchHourItem((int)h.DayOfWeek, CrmParsing.Format(h.OpenTime), CrmParsing.Format(h.CloseTime))).ToList(),
            detail.Closures.Select(c => new BranchClosureItem(c.Id, CrmParsing.Format(c.Date), c.Reason)).ToList()));
    }

    private static async Task<Ok<BranchResponse>> CreateAsync(SaveBranchRequest request, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken) =>
        TypedResults.Ok(BranchResponse.From(await branches.CreateAsync(tenant.RequireTenantId(), new BranchInput(request.Code, request.Name, request.Address, request.Phone), cancellationToken)));

    private static async Task<Ok<BranchResponse>> UpdateAsync(Guid branchId, SaveBranchRequest request, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken) =>
        TypedResults.Ok(BranchResponse.From(await branches.UpdateAsync(tenant.RequireTenantId(), branchId, new BranchInput(request.Code, request.Name, request.Address, request.Phone), cancellationToken)));

    private static async Task<NoContent> SetDefaultAsync(Guid branchId, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        await branches.SetDefaultAsync(tenant.RequireTenantId(), branchId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetStatusAsync(Guid branchId, SetBranchStatusRequest request, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        await branches.SetStatusAsync(tenant.RequireTenantId(), branchId, request.Status == "active" ? BranchStatus.Active : BranchStatus.Inactive, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetHoursAsync(Guid branchId, SetBranchHoursRequest request, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        var hours = request.Hours.Select(h => new BranchHourInput((DayOfWeek)h.DayOfWeek, CrmParsing.Time(h.OpenTime), CrmParsing.Time(h.CloseTime))).ToList();
        await branches.SetHoursAsync(tenant.RequireTenantId(), branchId, hours, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<BranchClosureItem>> AddClosureAsync(Guid branchId, AddBranchClosureRequest request, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        var closure = await branches.AddClosureAsync(tenant.RequireTenantId(), branchId, CrmParsing.Date(request.Date), request.Reason, cancellationToken);
        return TypedResults.Ok(new BranchClosureItem(closure.Id, CrmParsing.Format(closure.Date), closure.Reason));
    }

    private static async Task<NoContent> RemoveClosureAsync(Guid branchId, Guid closureId, ITenantContext tenant, BranchService branches, CancellationToken cancellationToken)
    {
        await branches.RemoveClosureAsync(tenant.RequireTenantId(), branchId, closureId, cancellationToken);
        return TypedResults.NoContent();
    }
}
