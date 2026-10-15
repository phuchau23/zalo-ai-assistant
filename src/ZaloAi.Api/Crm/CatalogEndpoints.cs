using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;

namespace ZaloAi.Api.Crm;

/// <param name="Kind">service | package | product</param>
/// <param name="Price">VNĐ; null = liên hệ báo giá.</param>
public sealed record CatalogItemResponse(
    Guid Id,
    string Kind,
    string Code,
    string Name,
    string? GroupName,
    long? Price,
    string? PriceNote,
    int? DurationMinutes,
    int BufferMinutes,
    int? SessionCount,
    int? ValidityDays,
    string? Sku,
    string? Unit,
    long? CostPrice,
    bool TrackInventory,
    bool IsActive,
    bool InKnowledge)
{
    internal static CatalogItemResponse From(CatalogItem c) => new(
        c.Id, c.Kind.ToString().ToLowerInvariant(), c.Code, c.Name, c.GroupName, c.Price, c.PriceNote, c.DurationMinutes, c.BufferMinutes,
        c.SessionCount, c.ValidityDays, c.Sku, c.Unit, c.CostPrice, c.TrackInventory, c.IsActive, c.KnowledgeItemId is not null);
}

public sealed record PackageContentItem(Guid ServiceId, int SessionCount);

/// <param name="BranchIds">Rỗng = bán ở mọi chi nhánh.</param>
public sealed record CatalogItemDetailResponse(CatalogItemResponse Item, IReadOnlyList<Guid> BranchIds, IReadOnlyList<PackageContentItem> Contents);

/// <param name="Kind">service | package | product (không đổi được sau khi tạo)</param>
public sealed record SaveCatalogItemRequest(
    string Kind,
    string Code,
    string Name,
    string? GroupName,
    long? Price,
    string? PriceNote,
    int? DurationMinutes,
    int BufferMinutes,
    int? SessionCount,
    int? ValidityDays,
    string? Sku,
    string? Unit,
    long? CostPrice,
    bool TrackInventory);

public sealed record SetCatalogBranchesRequest(IReadOnlyList<Guid> BranchIds);

public sealed record SetPackageContentsRequest(IReadOnlyList<PackageContentItem> Contents);

public sealed record SetCatalogActiveRequest(bool IsActive);

internal sealed class SaveCatalogItemRequestValidator : AbstractValidator<SaveCatalogItemRequest>
{
    public SaveCatalogItemRequestValidator()
    {
        RuleFor(x => x.Kind).Must(CatalogEndpoints.IsKind).WithMessage("Loại phải là service, package hoặc product.");
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50).WithName("Mã");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithName("Tên");
        RuleFor(x => x.GroupName).MaximumLength(100);
        RuleFor(x => x.PriceNote).MaximumLength(500);
        RuleFor(x => x.Sku).MaximumLength(100);
        RuleFor(x => x.Unit).MaximumLength(50);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).When(x => x.Price is not null);
        RuleFor(x => x.CostPrice).GreaterThanOrEqualTo(0).When(x => x.CostPrice is not null);
        RuleFor(x => x.BufferMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, 1440).When(x => x.DurationMinutes is not null);
        RuleFor(x => x.SessionCount).InclusiveBetween(1, 1000).When(x => x.SessionCount is not null);
        RuleFor(x => x.ValidityDays).InclusiveBetween(1, 3650).When(x => x.ValidityDays is not null);
    }
}

internal sealed class SetPackageContentsRequestValidator : AbstractValidator<SetPackageContentsRequest>
{
    public SetPackageContentsRequestValidator()
    {
        RuleFor(x => x.Contents).NotNull().Must(c => c.Count <= 50);
        RuleForEach(x => x.Contents).ChildRules(c => c.RuleFor(x => x.SessionCount).InclusiveBetween(1, 1000));
    }
}

internal sealed class SetCatalogBranchesRequestValidator : AbstractValidator<SetCatalogBranchesRequest>
{
    public SetCatalogBranchesRequestValidator() => RuleFor(x => x.BranchIds).NotNull().Must(b => b.Count <= 200);
}

/// <summary>Danh mục bán (CRM C1). Xem: mọi thành viên. Sửa: chủ hoặc quyền manage_catalog. Đổi giá → bot cũng thấy giá mới.</summary>
internal static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/catalog").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        read.MapGet("", ListAsync);
        read.MapGet("/{itemId:guid}", GetAsync).ProducesProblem(StatusCodes.Status404NotFound);

        var manage = app.MapGroup("/catalog").WithTags("CRM").RequirePermission(TenantPermissions.ManageCatalog);
        manage.MapPost("", CreateAsync).Validate<SaveCatalogItemRequest>().ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPut("/{itemId:guid}", UpdateAsync).Validate<SaveCatalogItemRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPatch("/{itemId:guid}/active", SetActiveAsync).ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPut("/{itemId:guid}/branches", SetBranchesAsync).Validate<SetCatalogBranchesRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPut("/{itemId:guid}/contents", SetContentsAsync).Validate<SetPackageContentsRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    internal static bool IsKind(string? kind) => kind is "service" or "package" or "product";

    private static CatalogKind ParseKind(string kind) => kind switch
    {
        "service" => CatalogKind.Service,
        "package" => CatalogKind.Package,
        _ => CatalogKind.Product,
    };

    /// <param name="kind">service | package | product; bỏ trống = tất cả.</param>
    /// <param name="branchId">Chỉ mục bán ở chi nhánh này.</param>
    private static async Task<Ok<List<CatalogItemResponse>>> ListAsync(
        string? kind, bool? includeInactive, Guid? branchId, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken)
    {
        var list = await catalog.ListAsync(tenant.RequireTenantId(), IsKind(kind) ? ParseKind(kind!) : null, includeInactive == true, branchId, cancellationToken);
        return TypedResults.Ok(list.Select(CatalogItemResponse.From).ToList());
    }

    private static async Task<Ok<CatalogItemDetailResponse>> GetAsync(Guid itemId, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken)
    {
        var detail = await catalog.GetAsync(tenant.RequireTenantId(), itemId, cancellationToken);
        return TypedResults.Ok(new CatalogItemDetailResponse(
            CatalogItemResponse.From(detail.Item),
            detail.BranchIds,
            detail.Contents.Select(c => new PackageContentItem(c.ServiceId, c.SessionCount)).ToList()));
    }

    private static async Task<Ok<CatalogItemResponse>> CreateAsync(SaveCatalogItemRequest request, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(CatalogItemResponse.From(await catalog.CreateAsync(tenant.RequireTenantId(), ToInput(request), cancellationToken)));

    private static async Task<Ok<CatalogItemResponse>> UpdateAsync(Guid itemId, SaveCatalogItemRequest request, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken) =>
        TypedResults.Ok(CatalogItemResponse.From(await catalog.UpdateAsync(tenant.RequireTenantId(), itemId, ToInput(request), cancellationToken)));

    private static async Task<NoContent> SetActiveAsync(Guid itemId, SetCatalogActiveRequest request, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken)
    {
        await catalog.SetActiveAsync(tenant.RequireTenantId(), itemId, request.IsActive, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetBranchesAsync(Guid itemId, SetCatalogBranchesRequest request, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken)
    {
        await catalog.SetBranchesAsync(tenant.RequireTenantId(), itemId, request.BranchIds, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetContentsAsync(Guid itemId, SetPackageContentsRequest request, ITenantContext tenant, CatalogService catalog, CancellationToken cancellationToken)
    {
        await catalog.SetPackageContentsAsync(tenant.RequireTenantId(), itemId, request.Contents.Select(c => new PackageContentInput(c.ServiceId, c.SessionCount)).ToList(), cancellationToken);
        return TypedResults.NoContent();
    }

    private static CatalogItemInput ToInput(SaveCatalogItemRequest r) => new(
        ParseKind(r.Kind), r.Code, r.Name, r.GroupName, r.Price, r.PriceNote, r.DurationMinutes, r.BufferMinutes, r.SessionCount, r.ValidityDays,
        r.Sku, r.Unit, r.CostPrice, r.TrackInventory);
}
