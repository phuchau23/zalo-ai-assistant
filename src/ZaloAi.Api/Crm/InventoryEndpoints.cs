using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Inventory;

namespace ZaloAi.Api.Crm;

public sealed record StockLevelResponse(Guid CatalogItemId, string Name, string? Sku, string? Unit, Guid BranchId, decimal Quantity, decimal? MinQuantity, long AvgCost, bool Low);

/// <param name="Kind">purchase | sale | return | adjust | transfer_out | transfer_in | consume</param>
public sealed record StockMovementResponse(Guid Id, Guid BranchId, Guid CatalogItemId, string Kind, decimal Quantity, decimal BalanceAfter, long? UnitCost, string? RefType, Guid? RefId, string? Note, DateTimeOffset At);

public sealed record SetStockMinimumRequest(Guid BranchId, Guid CatalogItemId, decimal? MinQuantity);

public sealed record AdjustStockRequest(Guid BranchId, Guid CatalogItemId, decimal Delta, string Reason);

public sealed record SupplierResponse(Guid Id, string Name, string? Phone, string? Note, bool IsActive)
{
    internal static SupplierResponse From(Supplier s) => new(s.Id, s.Name, s.Phone, s.Note, s.IsActive);
}

public sealed record SaveSupplierRequest(string Name, string? Phone, string? Note, bool IsActive = true);

public sealed record PurchaseLineRequest(Guid CatalogItemId, decimal Quantity, long UnitCost);

/// <param name="ReceiveNow">Nhận hàng ngay (tăng tồn) thay vì để nháp.</param>
public sealed record CreatePurchaseOrderRequest(Guid BranchId, Guid? SupplierId, IReadOnlyList<PurchaseLineRequest> Lines, string? Note, bool ReceiveNow = true);

public sealed record PurchaseLineResponse(Guid CatalogItemId, string Name, decimal Quantity, long UnitCost);

/// <param name="Status">draft | received | cancelled</param>
public sealed record PurchaseOrderResponse(Guid Id, int Number, Guid BranchId, Guid? SupplierId, string? SupplierName, string Status, long Total, string? Note, DateTimeOffset? ReceivedAt, DateTimeOffset CreatedAt, IReadOnlyList<PurchaseLineResponse> Lines)
{
    internal static PurchaseOrderResponse From(PurchaseOrderView v) => new(
        v.Order.Id, v.Order.Number, v.Order.BranchId, v.Order.SupplierId, v.SupplierName, v.Order.Status.ToString().ToLowerInvariant(), v.Order.Total, v.Order.Note, v.Order.ReceivedAt, v.Order.CreatedAt,
        v.Lines.Select(l => new PurchaseLineResponse(l.Line.CatalogItemId, l.Name, l.Line.Quantity, l.Line.UnitCost)).ToList());
}

public sealed record TransferLineRequest(Guid CatalogItemId, decimal Quantity);

public sealed record CreateTransferRequest(Guid FromBranchId, Guid ToBranchId, IReadOnlyList<TransferLineRequest> Lines, string? Note);

public sealed record CountLineRequest(Guid CatalogItemId, decimal CountedQuantity);

public sealed record CreateStockCountRequest(Guid BranchId, IReadOnlyList<CountLineRequest> Lines, string? Note, bool ApplyNow = true);

public sealed record StockCountLineResponse(Guid CatalogItemId, decimal ExpectedQuantity, decimal CountedQuantity);

/// <param name="Status">draft | applied</param>
public sealed record StockCountResponse(Guid Id, Guid BranchId, string Status, string? Note, DateTimeOffset? AppliedAt, DateTimeOffset CreatedAt, IReadOnlyList<StockCountLineResponse> Lines);

public sealed record ConsumableResponse(Guid ProductId, decimal QuantityPerSession);

public sealed record SetConsumablesRequest(IReadOnlyList<ConsumableResponse> Items);

internal sealed class AdjustStockRequestValidator : AbstractValidator<AdjustStockRequest>
{
    public AdjustStockRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.CatalogItemId).NotEmpty();
        RuleFor(x => x.Delta).NotEqual(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300).WithName("Lý do");
    }
}

internal sealed class SaveSupplierRequestValidator : AbstractValidator<SaveSupplierRequest>
{
    public SaveSupplierRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithName("Tên");
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class CreatePurchaseOrderRequestValidator : AbstractValidator<CreatePurchaseOrderRequest>
{
    public CreatePurchaseOrderRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Lines).NotNull().Must(l => l.Count is >= 1 and <= 200);
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Quantity).GreaterThan(0);
            l.RuleFor(x => x.UnitCost).GreaterThanOrEqualTo(0);
        });
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class CreateTransferRequestValidator : AbstractValidator<CreateTransferRequest>
{
    public CreateTransferRequestValidator()
    {
        RuleFor(x => x.FromBranchId).NotEmpty();
        RuleFor(x => x.ToBranchId).NotEmpty().NotEqual(x => x.FromBranchId);
        RuleFor(x => x.Lines).NotNull().Must(l => l.Count is >= 1 and <= 200);
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThan(0));
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class CreateStockCountRequestValidator : AbstractValidator<CreateStockCountRequest>
{
    public CreateStockCountRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Lines).NotNull().Must(l => l.Count is >= 1 and <= 500);
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.CountedQuantity).GreaterThanOrEqualTo(0));
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

internal sealed class SetConsumablesRequestValidator : AbstractValidator<SetConsumablesRequest>
{
    public SetConsumablesRequestValidator()
    {
        RuleFor(x => x.Items).NotNull().Must(i => i.Count <= 50);
        RuleForEach(x => x.Items).ChildRules(i => i.RuleFor(x => x.QuantityPerSession).GreaterThan(0));
    }
}

/// <summary>Kho hàng (CRM C4). Xem: mọi thành viên (giới hạn chi nhánh); ghi: quyền manage_inventory.</summary>
internal static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/inventory").WithTags("CRM").RequireTenantRole(TenantRole.Staff);
        read.MapGet("/levels", LevelsAsync);
        read.MapGet("/movements", MovementsAsync);
        read.MapGet("/suppliers", SuppliersAsync);
        read.MapGet("/purchase-orders", PurchaseOrdersAsync);
        read.MapGet("/purchase-orders/{orderId:guid}", GetPurchaseOrderAsync).ProducesProblem(StatusCodes.Status404NotFound);
        read.MapGet("/counts", CountsAsync);
        read.MapGet("/consumables/{serviceId:guid}", ConsumablesAsync);

        var manage = app.MapGroup("/inventory").WithTags("CRM").RequirePermission(TenantPermissions.ManageInventory);
        manage.MapPut("/minimum", SetMinimumAsync).ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/adjust", AdjustAsync).Validate<AdjustStockRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/suppliers", CreateSupplierAsync).Validate<SaveSupplierRequest>();
        manage.MapPut("/suppliers/{supplierId:guid}", UpdateSupplierAsync).Validate<SaveSupplierRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/purchase-orders", CreatePurchaseOrderAsync).Validate<CreatePurchaseOrderRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/purchase-orders/{orderId:guid}/receive", ReceivePurchaseOrderAsync).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPost("/purchase-orders/{orderId:guid}/cancel", CancelPurchaseOrderAsync).ProducesProblem(StatusCodes.Status409Conflict);
        manage.MapPost("/transfers", TransferAsync).Validate<CreateTransferRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/counts", CreateCountAsync).Validate<CreateStockCountRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPost("/counts/{countId:guid}/apply", ApplyCountAsync).ProducesProblem(StatusCodes.Status404NotFound);
        manage.MapPut("/consumables/{serviceId:guid}", SetConsumablesAsync).Validate<SetConsumablesRequest>().ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<List<StockLevelResponse>>> LevelsAsync(Guid? branchId, bool? lowOnly, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok((await inventory.LevelsAsync(tenant.RequireTenantId(), branchId, lowOnly == true, cancellationToken))
            .Select(r => new StockLevelResponse(r.CatalogItemId, r.Name, r.Sku, r.Unit, r.BranchId, r.Quantity, r.MinQuantity, r.AvgCost, r.Low)).ToList());

    private static async Task<Ok<List<StockMovementResponse>>> MovementsAsync(Guid? branchId, Guid? catalogItemId, int? limit, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok((await inventory.MovementsAsync(tenant.RequireTenantId(), branchId, catalogItemId, limit ?? 100, cancellationToken))
            .Select(m => new StockMovementResponse(m.Id, m.BranchId, m.CatalogItemId, AppointmentResponse.Snake(m.Kind), m.Quantity, m.BalanceAfter, m.UnitCost, m.RefType, m.RefId, m.Note, m.At)).ToList());

    private static async Task<NoContent> SetMinimumAsync(SetStockMinimumRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        await inventory.SetMinimumAsync(tenant.RequireTenantId(), request.BranchId, request.CatalogItemId, request.MinQuantity, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<StockMovementResponse>> AdjustAsync(AdjustStockRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        var m = await inventory.AdjustAsync(tenant.RequireTenantId(), request.BranchId, request.CatalogItemId, request.Delta, request.Reason, cancellationToken);
        return TypedResults.Ok(new StockMovementResponse(m.Id, m.BranchId, m.CatalogItemId, AppointmentResponse.Snake(m.Kind), m.Quantity, m.BalanceAfter, m.UnitCost, m.RefType, m.RefId, m.Note, m.At));
    }

    private static async Task<Ok<List<SupplierResponse>>> SuppliersAsync(ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok((await inventory.SuppliersAsync(tenant.RequireTenantId(), cancellationToken)).Select(SupplierResponse.From).ToList());

    private static async Task<Ok<SupplierResponse>> CreateSupplierAsync(SaveSupplierRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok(SupplierResponse.From(await inventory.SaveSupplierAsync(tenant.RequireTenantId(), null, request.Name, request.Phone, request.Note, request.IsActive, cancellationToken)));

    private static async Task<Ok<SupplierResponse>> UpdateSupplierAsync(Guid supplierId, SaveSupplierRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok(SupplierResponse.From(await inventory.SaveSupplierAsync(tenant.RequireTenantId(), supplierId, request.Name, request.Phone, request.Note, request.IsActive, cancellationToken)));

    private static async Task<Ok<List<PurchaseOrderResponse>>> PurchaseOrdersAsync(Guid? branchId, int? limit, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok((await inventory.PurchaseOrdersAsync(tenant.RequireTenantId(), branchId, limit ?? 50, cancellationToken)).Select(PurchaseOrderResponse.From).ToList());

    private static async Task<Ok<PurchaseOrderResponse>> GetPurchaseOrderAsync(Guid orderId, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok(PurchaseOrderResponse.From(await inventory.GetPurchaseOrderAsync(tenant.RequireTenantId(), orderId, cancellationToken)));

    private static async Task<Ok<PurchaseOrderResponse>> CreatePurchaseOrderAsync(CreatePurchaseOrderRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var order = await inventory.CreatePurchaseOrderAsync(tenantId, request.BranchId, request.SupplierId, request.Lines.Select(l => new PurchaseLineInput(l.CatalogItemId, l.Quantity, l.UnitCost)).ToList(), request.Note, request.ReceiveNow, cancellationToken);
        return TypedResults.Ok(PurchaseOrderResponse.From(await inventory.GetPurchaseOrderAsync(tenantId, order.Id, cancellationToken)));
    }

    private static async Task<Ok<PurchaseOrderResponse>> ReceivePurchaseOrderAsync(Guid orderId, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await inventory.ReceivePurchaseOrderAsync(tenantId, orderId, cancellationToken);
        return TypedResults.Ok(PurchaseOrderResponse.From(await inventory.GetPurchaseOrderAsync(tenantId, orderId, cancellationToken)));
    }

    private static async Task<NoContent> CancelPurchaseOrderAsync(Guid orderId, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        await inventory.CancelPurchaseOrderAsync(tenant.RequireTenantId(), orderId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> TransferAsync(CreateTransferRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        await inventory.TransferAsync(tenant.RequireTenantId(), request.FromBranchId, request.ToBranchId, request.Lines.Select(l => new TransferLineInput(l.CatalogItemId, l.Quantity)).ToList(), request.Note, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<StockCountResponse>>> CountsAsync(Guid? branchId, int? limit, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok((await inventory.CountsAsync(tenant.RequireTenantId(), branchId, limit ?? 30, cancellationToken)).Select(ToResponse).ToList());

    private static async Task<Ok<StockCountResponse>> CreateCountAsync(CreateStockCountRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var count = await inventory.CreateCountAsync(tenantId, request.BranchId, request.Lines.Select(l => new CountLineInput(l.CatalogItemId, l.CountedQuantity)).ToList(), request.Note, request.ApplyNow, cancellationToken);
        var row = (await inventory.CountsAsync(tenantId, request.BranchId, 100, cancellationToken)).First(c => c.Count.Id == count.Id);
        return TypedResults.Ok(ToResponse(row));
    }

    private static async Task<NoContent> ApplyCountAsync(Guid countId, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        await inventory.ApplyCountAsync(tenant.RequireTenantId(), countId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<ConsumableResponse>>> ConsumablesAsync(Guid serviceId, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken) =>
        TypedResults.Ok((await inventory.ConsumablesAsync(tenant.RequireTenantId(), serviceId, cancellationToken)).Select(c => new ConsumableResponse(c.ProductId, c.QuantityPerSession)).ToList());

    private static async Task<NoContent> SetConsumablesAsync(Guid serviceId, SetConsumablesRequest request, ITenantContext tenant, InventoryService inventory, CancellationToken cancellationToken)
    {
        await inventory.SetConsumablesAsync(tenant.RequireTenantId(), serviceId, request.Items.Select(i => new ConsumableInput(i.ProductId, i.QuantityPerSession)).ToList(), cancellationToken);
        return TypedResults.NoContent();
    }

    private static StockCountResponse ToResponse((StockCount Count, IReadOnlyList<StockCountLine> Lines) row) => new(
        row.Count.Id, row.Count.BranchId, row.Count.Status.ToString().ToLowerInvariant(), row.Count.Note, row.Count.AppliedAt, row.Count.CreatedAt,
        row.Lines.Select(l => new StockCountLineResponse(l.CatalogItemId, l.ExpectedQuantity, l.CountedQuantity)).ToList());
}
