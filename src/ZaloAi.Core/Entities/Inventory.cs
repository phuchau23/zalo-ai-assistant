namespace ZaloAi.Core.Entities;

public enum StockMovementKind
{
    /// <summary>Nhập hàng (phiếu nhập).</summary>
    Purchase = 0,

    /// <summary>Bán (hóa đơn thu đủ).</summary>
    Sale = 1,

    /// <summary>Hoàn / hủy hóa đơn → trả về kho.</summary>
    Return = 2,

    /// <summary>Điều chỉnh tay / kiểm kê.</summary>
    Adjust = 3,

    TransferOut = 4,
    TransferIn = 5,

    /// <summary>Tiêu hao vật tư theo định mức dịch vụ (khi hoàn thành buổi làm).</summary>
    Consume = 6,
}

public enum PurchaseOrderStatus
{
    Draft = 0,
    Received = 1,
    Cancelled = 2,
}

public enum StockCountStatus
{
    Draft = 0,
    Applied = 1,
}

public sealed class Supplier : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Phone { get; set; }

    public string? Note { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Tồn kho một sản phẩm tại một chi nhánh (CRM C4). Số lượng có thể âm (bán trước nhập sau) — báo cáo cảnh báo.</summary>
public sealed class StockLevel : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid CatalogItemId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Dưới mức này → cảnh báo sắp hết; null = không cảnh báo.</summary>
    public decimal? MinQuantity { get; set; }

    /// <summary>Giá vốn bình quân (VNĐ/đơn vị) cập nhật theo phiếu nhập.</summary>
    public long AvgCost { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Mọi thay đổi tồn kho là một dòng (có dấu). Không sửa/xóa dòng cũ.</summary>
public sealed class StockMovement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid CatalogItemId { get; set; }

    public StockMovementKind Kind { get; set; }

    /// <summary>Dương = vào kho, âm = ra kho.</summary>
    public decimal Quantity { get; set; }

    public decimal BalanceAfter { get; set; }

    public long? UnitCost { get; set; }

    /// <summary>invoice | purchase_order | transfer | stock_count | appointment | manual</summary>
    public string? RefType { get; set; }

    public Guid? RefId { get; set; }

    public string? Note { get; set; }

    public Guid? ByUserId { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class PurchaseOrder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid? SupplierId { get; set; }

    public int Number { get; set; }

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    public long Total { get; set; }

    public string? Note { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PurchaseOrderLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid PurchaseOrderId { get; set; }

    public Guid CatalogItemId { get; set; }

    public decimal Quantity { get; set; }

    public long UnitCost { get; set; }
}

public sealed class StockTransfer : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid FromBranchId { get; set; }

    public Guid ToBranchId { get; set; }

    public string? Note { get; set; }

    public Guid? ByUserId { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class StockTransferLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StockTransferId { get; set; }

    public Guid CatalogItemId { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>Kiểm kê: đếm thực tế → áp dụng tạo dòng điều chỉnh chênh lệch.</summary>
public sealed class StockCount : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public StockCountStatus Status { get; set; } = StockCountStatus.Draft;

    public string? Note { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StockCountLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StockCountId { get; set; }

    public Guid CatalogItemId { get; set; }

    public decimal ExpectedQuantity { get; set; }

    public decimal CountedQuantity { get; set; }
}

/// <summary>Định mức vật tư: mỗi buổi dịch vụ tiêu hao bao nhiêu sản phẩm (tự trừ kho khi hoàn thành buổi).</summary>
public sealed class ServiceConsumable : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid ServiceId { get; set; }

    public Guid ProductId { get; set; }

    public decimal QuantityPerSession { get; set; }
}
