using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

// CRM C4 (docs/CRM-PLAN.md): kho hàng theo chi nhánh.

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("suppliers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.Phone).HasMaxLength(30);
        builder.Property(s => s.Note).HasMaxLength(500);
        builder.HasIndex(s => s.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        builder.ToTable("stock_levels");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Quantity).HasPrecision(14, 3);
        builder.Property(s => s.MinQuantity).HasPrecision(14, 3);
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.CatalogItemId }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(s => s.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<StockMovementKind>>();
        builder.Property(m => m.Quantity).HasPrecision(14, 3);
        builder.Property(m => m.BalanceAfter).HasPrecision(14, 3);
        builder.Property(m => m.RefType).HasMaxLength(30);
        builder.Property(m => m.Note).HasMaxLength(300);
        builder.HasIndex(m => new { m.TenantId, m.BranchId, m.CatalogItemId, m.At });
        builder.HasIndex(m => new { m.TenantId, m.RefType, m.RefId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(m => m.BranchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(m => m.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<PurchaseOrderStatus>>();
        builder.Property(p => p.Note).HasMaxLength(500);
        builder.HasIndex(p => new { p.TenantId, p.Number }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.BranchId, p.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(p => p.BranchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("purchase_order_lines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Quantity).HasPrecision(14, 3);
        builder.HasIndex(l => new { l.TenantId, l.PurchaseOrderId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(l => l.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> builder)
    {
        builder.ToTable("stock_transfers");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Note).HasMaxLength(500);
        builder.HasIndex(t => new { t.TenantId, t.At });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(t => t.FromBranchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(t => t.ToBranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> builder)
    {
        builder.ToTable("stock_transfer_lines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Quantity).HasPrecision(14, 3);
        builder.HasIndex(l => new { l.TenantId, l.StockTransferId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StockTransfer>().WithMany().HasForeignKey(l => l.StockTransferId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(l => l.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> builder)
    {
        builder.ToTable("stock_counts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<StockCountStatus>>();
        builder.Property(c => c.Note).HasMaxLength(500);
        builder.HasIndex(c => new { c.TenantId, c.BranchId, c.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(c => c.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StockCountLineConfiguration : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> builder)
    {
        builder.ToTable("stock_count_lines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.ExpectedQuantity).HasPrecision(14, 3);
        builder.Property(l => l.CountedQuantity).HasPrecision(14, 3);
        builder.HasIndex(l => new { l.TenantId, l.StockCountId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StockCount>().WithMany().HasForeignKey(l => l.StockCountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(l => l.CatalogItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServiceConsumableConfiguration : IEntityTypeConfiguration<ServiceConsumable>
{
    public void Configure(EntityTypeBuilder<ServiceConsumable> builder)
    {
        builder.ToTable("service_consumables");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.QuantityPerSession).HasPrecision(14, 3);
        builder.HasIndex(c => new { c.TenantId, c.ServiceId, c.ProductId }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(c => c.ServiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
