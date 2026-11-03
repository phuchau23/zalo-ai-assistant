using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Inventory;

public sealed record StockRow(Guid CatalogItemId, string Name, string? Sku, string? Unit, Guid BranchId, decimal Quantity, decimal? MinQuantity, long AvgCost, bool Low);

public sealed record PurchaseLineInput(Guid CatalogItemId, decimal Quantity, long UnitCost);

public sealed record PurchaseOrderView(PurchaseOrder Order, IReadOnlyList<(PurchaseOrderLine Line, string Name)> Lines, string? SupplierName);

public sealed record TransferLineInput(Guid CatalogItemId, decimal Quantity);

public sealed record CountLineInput(Guid CatalogItemId, decimal CountedQuantity);

public sealed record ConsumableInput(Guid ProductId, decimal QuantityPerSession);

/// <summary>
/// Kho hàng theo chi nhánh (CRM C4). Mọi thay đổi tồn = một dòng <see cref="StockMovement"/> có số dư sau; tồn có thể âm (cảnh báo).
/// Hóa đơn thu đủ trừ sản phẩm; hoàn thành buổi làm trừ vật tư theo định mức; hủy hóa đơn trả lại. Ghi: quyền manage_inventory.
/// </summary>
public sealed class InventoryService(AppDbContext db, ITenantContext tenantContext, BranchService branches, TimeProvider time) : TenantScopedRepository(db, tenantContext)
{
    private readonly ITenantContext _caller = tenantContext;

    public async Task<IReadOnlyList<StockRow>> LevelsAsync(Guid tenantId, Guid? branchId, bool lowOnly, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var branch = _caller.BranchId ?? branchId;
        var products = await Db.CatalogItems.AsNoTracking().Where(c => c.TenantId == tenantId && c.Kind == CatalogKind.Product && c.TrackInventory && c.IsActive).ToListAsync(cancellationToken);
        var levels = await Db.StockLevels.AsNoTracking().Where(s => s.TenantId == tenantId && (branch == null || s.BranchId == branch)).ToListAsync(cancellationToken);
        var branchIds = branch is { } b ? [b] : (await branches.ListAsync(tenantId, includeInactive: false, cancellationToken)).Select(x => x.Id).ToList();
        var rows = new List<StockRow>();
        foreach (var bid in branchIds)
        {
            foreach (var p in products)
            {
                var level = levels.FirstOrDefault(l => l.BranchId == bid && l.CatalogItemId == p.Id);
                var qty = level?.Quantity ?? 0;
                var low = level?.MinQuantity is { } min && qty <= min;
                if (!lowOnly || low)
                {
                    rows.Add(new StockRow(p.Id, p.Name, p.Sku, p.Unit, bid, qty, level?.MinQuantity, level?.AvgCost ?? p.CostPrice ?? 0, low));
                }
            }
        }

        return rows;
    }

    public async Task<IReadOnlyList<StockMovement>> MovementsAsync(Guid tenantId, Guid? branchId, Guid? catalogItemId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.StockMovements.AsNoTracking().Where(m => m.TenantId == tenantId);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(m => m.BranchId == b);
        }

        if (catalogItemId is { } c)
        {
            query = query.Where(m => m.CatalogItemId == c);
        }

        return await query.OrderByDescending(m => m.At).Take(Math.Clamp(limit, 1, 500)).ToListAsync(cancellationToken);
    }

    public async Task SetMinimumAsync(Guid tenantId, Guid branchId, Guid catalogItemId, decimal? minQuantity, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        await branches.RequireAsync(tenantId, branchId, cancellationToken);
        var level = await LevelAsync(tenantId, branchId, catalogItemId, cancellationToken);
        level.MinQuantity = minQuantity is < 0 ? throw new InvalidInputException("Mức tối thiểu không âm.") : minQuantity;
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Điều chỉnh tay (+/−) với lý do.</summary>
    public async Task<StockMovement> AdjustAsync(Guid tenantId, Guid branchId, Guid catalogItemId, decimal delta, string reason, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (delta == 0 || string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidInputException("Cần số lượng khác 0 và lý do.");
        }

        await branches.RequireAsync(tenantId, branchId, cancellationToken);
        await RequireProductAsync(tenantId, catalogItemId, cancellationToken);
        var movement = await MoveAsync(tenantId, branchId, catalogItemId, StockMovementKind.Adjust, delta, null, "manual", null, reason.Trim(), cancellationToken);
        Audit(tenantId, "inventory.adjusted", $"{catalogItemId}:{delta}");
        await Db.SaveChangesAsync(cancellationToken);
        return movement;
    }

    // ---- Nhà cung cấp ----

    public async Task<IReadOnlyList<Supplier>> SuppliersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.Suppliers.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(cancellationToken);
    }

    public async Task<Supplier> SaveSupplierAsync(Guid tenantId, Guid? supplierId, string name, string? phone, string? note, bool isActive, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidInputException("Cần tên nhà cung cấp.");
        }

        var supplier = supplierId is { } id
            ? await Db.Suppliers.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, cancellationToken) ?? throw new NotFoundException()
            : Db.Suppliers.Add(new Supplier { TenantId = tenantId, Name = "" }).Entity;
        supplier.Name = name.Trim();
        supplier.Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        supplier.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        supplier.IsActive = isActive;
        await Db.SaveChangesAsync(cancellationToken);
        return supplier;
    }

    // ---- Phiếu nhập ----

    public async Task<IReadOnlyList<PurchaseOrderView>> PurchaseOrdersAsync(Guid tenantId, Guid? branchId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.PurchaseOrders.AsNoTracking().Where(p => p.TenantId == tenantId);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(p => p.BranchId == b);
        }

        var orders = await query.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        var views = new List<PurchaseOrderView>();
        foreach (var order in orders)
        {
            views.Add(await PurchaseViewAsync(tenantId, order, cancellationToken));
        }

        return views;
    }

    public async Task<PurchaseOrderView> GetPurchaseOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
        await PurchaseViewAsync(tenantId, await RequirePurchaseAsync(tenantId, orderId, cancellationToken), cancellationToken);

    public async Task<PurchaseOrder> CreatePurchaseOrderAsync(Guid tenantId, Guid branchId, Guid? supplierId, IReadOnlyList<PurchaseLineInput> lines, string? note, bool receiveNow, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(lines);
        await branches.RequireAsync(tenantId, branchId, cancellationToken);
        if (lines.Count == 0 || lines.Any(l => l.Quantity <= 0 || l.UnitCost < 0))
        {
            throw new InvalidInputException("Phiếu nhập cần ít nhất một dòng, số lượng > 0, giá vốn không âm.");
        }

        if (supplierId is { } sid && !await Db.Suppliers.AnyAsync(s => s.TenantId == tenantId && s.Id == sid, cancellationToken))
        {
            throw new NotFoundException();
        }

        foreach (var line in lines)
        {
            await RequireProductAsync(tenantId, line.CatalogItemId, cancellationToken);
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var order = new PurchaseOrder
        {
            TenantId = tenantId,
            BranchId = branchId,
            SupplierId = supplierId,
            Number = await NextNumberAsync(tenantId, cancellationToken),
            Total = lines.Sum(l => (long)Math.Round(l.Quantity * l.UnitCost)),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedByUserId = _caller.UserId,
        };
        Db.PurchaseOrders.Add(order);
        foreach (var line in lines)
        {
            Db.PurchaseOrderLines.Add(new PurchaseOrderLine { TenantId = tenantId, PurchaseOrderId = order.Id, CatalogItemId = line.CatalogItemId, Quantity = line.Quantity, UnitCost = line.UnitCost });
        }

        Audit(tenantId, "purchase_order.created", order.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        if (receiveNow)
        {
            await ReceiveCoreAsync(tenantId, order, cancellationToken);
            await Db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return order;
    }

    public async Task<PurchaseOrder> ReceivePurchaseOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await RequirePurchaseAsync(tenantId, orderId, cancellationToken);
        if (order.Status != PurchaseOrderStatus.Draft)
        {
            throw new ConflictException("Phiếu nhập đã nhận hoặc đã hủy.", "purchase_state");
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        await ReceiveCoreAsync(tenantId, order, cancellationToken);
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return order;
    }

    public async Task CancelPurchaseOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await RequirePurchaseAsync(tenantId, orderId, cancellationToken);
        if (order.Status != PurchaseOrderStatus.Draft)
        {
            throw new ConflictException("Chỉ hủy phiếu nhập chưa nhận.", "purchase_state");
        }

        order.Status = PurchaseOrderStatus.Cancelled;
        Audit(tenantId, "purchase_order.cancelled", order.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    // ---- Chuyển kho ----

    public async Task<StockTransfer> TransferAsync(Guid tenantId, Guid fromBranchId, Guid toBranchId, IReadOnlyList<TransferLineInput> lines, string? note, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(lines);
        if (fromBranchId == toBranchId || lines.Count == 0 || lines.Any(l => l.Quantity <= 0))
        {
            throw new InvalidInputException("Chuyển kho cần hai chi nhánh khác nhau và số lượng > 0.");
        }

        await branches.RequireAsync(tenantId, fromBranchId, cancellationToken);
        if (!await Db.Branches.AnyAsync(b => b.TenantId == tenantId && b.Id == toBranchId, cancellationToken))
        {
            throw new NotFoundException();
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var transfer = new StockTransfer { TenantId = tenantId, FromBranchId = fromBranchId, ToBranchId = toBranchId, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ByUserId = _caller.UserId, At = time.GetUtcNow() };
        Db.StockTransfers.Add(transfer);
        foreach (var line in lines)
        {
            await RequireProductAsync(tenantId, line.CatalogItemId, cancellationToken);
            Db.StockTransferLines.Add(new StockTransferLine { TenantId = tenantId, StockTransferId = transfer.Id, CatalogItemId = line.CatalogItemId, Quantity = line.Quantity });
            var from = await LevelAsync(tenantId, fromBranchId, line.CatalogItemId, cancellationToken);
            await MoveAsync(tenantId, fromBranchId, line.CatalogItemId, StockMovementKind.TransferOut, -line.Quantity, from.AvgCost, "transfer", transfer.Id, null, cancellationToken);
            await MoveAsync(tenantId, toBranchId, line.CatalogItemId, StockMovementKind.TransferIn, line.Quantity, from.AvgCost, "transfer", transfer.Id, null, cancellationToken);
        }

        Audit(tenantId, "inventory.transferred", transfer.Id.ToString());
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return transfer;
    }

    // ---- Kiểm kê ----

    public async Task<StockCount> CreateCountAsync(Guid tenantId, Guid branchId, IReadOnlyList<CountLineInput> lines, string? note, bool applyNow, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(lines);
        await branches.RequireAsync(tenantId, branchId, cancellationToken);
        if (lines.Count == 0 || lines.Any(l => l.CountedQuantity < 0))
        {
            throw new InvalidInputException("Kiểm kê cần ít nhất một dòng, số đếm không âm.");
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var count = new StockCount { TenantId = tenantId, BranchId = branchId, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedByUserId = _caller.UserId };
        Db.StockCounts.Add(count);
        foreach (var line in lines)
        {
            await RequireProductAsync(tenantId, line.CatalogItemId, cancellationToken);
            var level = await LevelAsync(tenantId, branchId, line.CatalogItemId, cancellationToken);
            Db.StockCountLines.Add(new StockCountLine { TenantId = tenantId, StockCountId = count.Id, CatalogItemId = line.CatalogItemId, ExpectedQuantity = level.Quantity, CountedQuantity = line.CountedQuantity });
        }

        await Db.SaveChangesAsync(cancellationToken);
        if (applyNow)
        {
            await ApplyCountCoreAsync(tenantId, count, cancellationToken);
            await Db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<StockCount> ApplyCountAsync(Guid tenantId, Guid countId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var count = await Db.StockCounts.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == countId, cancellationToken) ?? throw new NotFoundException();
        if (count.Status == StockCountStatus.Applied)
        {
            return count;
        }

        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        await ApplyCountCoreAsync(tenantId, count, cancellationToken);
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<IReadOnlyList<(StockCount Count, IReadOnlyList<StockCountLine> Lines)>> CountsAsync(Guid tenantId, Guid? branchId, int limit, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = Db.StockCounts.AsNoTracking().Where(c => c.TenantId == tenantId);
        if ((_caller.BranchId ?? branchId) is { } b)
        {
            query = query.Where(c => c.BranchId == b);
        }

        var counts = await query.OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(cancellationToken);
        var ids = counts.Select(c => c.Id).ToList();
        var lines = await Db.StockCountLines.AsNoTracking().Where(l => l.TenantId == tenantId && ids.Contains(l.StockCountId)).ToListAsync(cancellationToken);
        return counts.Select(c => (c, (IReadOnlyList<StockCountLine>)lines.Where(l => l.StockCountId == c.Id).ToList())).ToList();
    }

    // ---- Định mức vật tư ----

    public async Task<IReadOnlyList<ServiceConsumable>> ConsumablesAsync(Guid tenantId, Guid serviceId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        return await Db.ServiceConsumables.AsNoTracking().Where(c => c.TenantId == tenantId && c.ServiceId == serviceId).ToListAsync(cancellationToken);
    }

    public async Task SetConsumablesAsync(Guid tenantId, Guid serviceId, IReadOnlyList<ConsumableInput> inputs, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(inputs);
        if (!await Db.CatalogItems.AnyAsync(c => c.TenantId == tenantId && c.Id == serviceId && c.Kind == CatalogKind.Service, cancellationToken))
        {
            throw new NotFoundException();
        }

        if (inputs.Any(i => i.QuantityPerSession <= 0) || inputs.Select(i => i.ProductId).Distinct().Count() != inputs.Count)
        {
            throw new InvalidInputException("Mỗi sản phẩm một dòng, định mức > 0.");
        }

        foreach (var input in inputs)
        {
            await RequireProductAsync(tenantId, input.ProductId, cancellationToken);
        }

        await Db.ServiceConsumables.Where(c => c.TenantId == tenantId && c.ServiceId == serviceId).ExecuteDeleteAsync(cancellationToken);
        foreach (var input in inputs)
        {
            Db.ServiceConsumables.Add(new ServiceConsumable { TenantId = tenantId, ServiceId = serviceId, ProductId = input.ProductId, QuantityPerSession = input.QuantityPerSession });
        }

        Audit(tenantId, "inventory.consumables_updated", serviceId.ToString());
        await Db.SaveChangesAsync(cancellationToken);
    }

    // ---- Hook từ bán hàng / lịch hẹn (gọi trong transaction của bên gọi, không SaveChanges) ----

    /// <summary>Hóa đơn thu đủ: trừ sản phẩm có theo dõi tồn; hóa đơn không gắn lịch hẹn thì trừ cả vật tư dịch vụ.</summary>
    internal async Task OnInvoiceSettledAsync(Guid tenantId, Guid invoiceId, Guid branchId, bool consumeServices, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var lines = await Db.InvoiceLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.InvoiceId == invoiceId && l.CatalogItemId != null).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            if (line.Kind == InvoiceLineKind.Product)
            {
                var product = await Db.CatalogItems.AsNoTracking().FirstOrDefaultAsync(c => c.Id == line.CatalogItemId, cancellationToken);
                if (product is { TrackInventory: true })
                {
                    await MoveAsync(tenantId, branchId, product.Id, StockMovementKind.Sale, -line.Quantity, null, "invoice", invoiceId, null, cancellationToken);
                }
            }
            else if (consumeServices && line.Kind is InvoiceLineKind.Service or InvoiceLineKind.Redemption)
            {
                await ConsumeAsync(tenantId, branchId, line.CatalogItemId!.Value, line.Quantity, "invoice", invoiceId, cancellationToken);
            }
        }
    }

    /// <summary>Hủy hóa đơn: trả lại sản phẩm (và vật tư) đã trừ bởi hóa đơn đó.</summary>
    internal async Task OnInvoiceVoidedAsync(Guid tenantId, Guid invoiceId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var movements = await Db.StockMovements.AsNoTracking().Where(m => m.TenantId == tenantId && m.RefType == "invoice" && m.RefId == invoiceId && m.Quantity < 0).ToListAsync(cancellationToken);
        foreach (var m in movements)
        {
            await MoveAsync(tenantId, m.BranchId, m.CatalogItemId, StockMovementKind.Return, -m.Quantity, m.UnitCost, "invoice_void", invoiceId, null, cancellationToken);
        }
    }

    /// <summary>Hoàn thành buổi làm: trừ vật tư theo định mức các dịch vụ của lịch (idempotent theo lịch).</summary>
    public async Task OnAppointmentDoneAsync(Guid tenantId, Appointment appointment, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(appointment);
        if (await Db.StockMovements.AnyAsync(m => m.TenantId == tenantId && m.RefType == "appointment" && m.RefId == appointment.Id, cancellationToken))
        {
            return;
        }

        var items = await Db.AppointmentItems.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppointmentId == appointment.Id).ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            await ConsumeAsync(tenantId, appointment.BranchId, item.CatalogItemId, 1, "appointment", appointment.Id, cancellationToken);
        }
    }

    private async Task ConsumeAsync(Guid tenantId, Guid branchId, Guid serviceId, int sessions, string refType, Guid refId, CancellationToken cancellationToken)
    {
        var consumables = await Db.ServiceConsumables.AsNoTracking().Where(c => c.TenantId == tenantId && c.ServiceId == serviceId).ToListAsync(cancellationToken);
        foreach (var c in consumables)
        {
            await MoveAsync(tenantId, branchId, c.ProductId, StockMovementKind.Consume, -c.QuantityPerSession * sessions, null, refType, refId, null, cancellationToken);
        }
    }

    // ---- nội bộ ----

    private async Task ReceiveCoreAsync(Guid tenantId, PurchaseOrder order, CancellationToken cancellationToken)
    {
        var lines = await Db.PurchaseOrderLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.PurchaseOrderId == order.Id).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            var level = await LevelAsync(tenantId, order.BranchId, line.CatalogItemId, cancellationToken);
            // Giá vốn bình quân gia quyền (tồn âm → lấy giá nhập mới).
            var existingQty = Math.Max(0, level.Quantity);
            var newQty = existingQty + line.Quantity;
            level.AvgCost = newQty > 0 ? (long)Math.Round((existingQty * level.AvgCost + line.Quantity * line.UnitCost) / newQty) : line.UnitCost;
            await MoveAsync(tenantId, order.BranchId, line.CatalogItemId, StockMovementKind.Purchase, line.Quantity, line.UnitCost, "purchase_order", order.Id, null, cancellationToken);
        }

        order.Status = PurchaseOrderStatus.Received;
        order.ReceivedAt = time.GetUtcNow();
        Audit(tenantId, "purchase_order.received", order.Id.ToString());
    }

    private async Task ApplyCountCoreAsync(Guid tenantId, StockCount count, CancellationToken cancellationToken)
    {
        var lines = await Db.StockCountLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.StockCountId == count.Id).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            var level = await LevelAsync(tenantId, count.BranchId, line.CatalogItemId, cancellationToken);
            var delta = line.CountedQuantity - level.Quantity;
            if (delta != 0)
            {
                await MoveAsync(tenantId, count.BranchId, line.CatalogItemId, StockMovementKind.Adjust, delta, null, "stock_count", count.Id, "Kiểm kê", cancellationToken);
            }
        }

        count.Status = StockCountStatus.Applied;
        count.AppliedAt = time.GetUtcNow();
        Audit(tenantId, "stock_count.applied", count.Id.ToString());
    }

    private async Task<StockMovement> MoveAsync(Guid tenantId, Guid branchId, Guid catalogItemId, StockMovementKind kind, decimal quantity, long? unitCost, string refType, Guid? refId, string? note, CancellationToken cancellationToken)
    {
        var level = await LevelAsync(tenantId, branchId, catalogItemId, cancellationToken);
        level.Quantity += quantity;
        level.UpdatedAt = time.GetUtcNow();
        var movement = new StockMovement
        {
            TenantId = tenantId,
            BranchId = branchId,
            CatalogItemId = catalogItemId,
            Kind = kind,
            Quantity = quantity,
            BalanceAfter = level.Quantity,
            UnitCost = unitCost ?? level.AvgCost,
            RefType = refType,
            RefId = refId,
            Note = note,
            ByUserId = _caller.UserId,
            At = time.GetUtcNow(),
        };
        Db.StockMovements.Add(movement);
        return movement;
    }

    /// <summary>Dòng tồn (track). Chưa có → tạo (chỉ trong change tracker). Tránh tạo trùng trong cùng scope.</summary>
    private async Task<StockLevel> LevelAsync(Guid tenantId, Guid branchId, Guid catalogItemId, CancellationToken cancellationToken)
    {
        var tracked = Db.ChangeTracker.Entries<StockLevel>().Select(e => e.Entity).FirstOrDefault(l => l.TenantId == tenantId && l.BranchId == branchId && l.CatalogItemId == catalogItemId);
        if (tracked is not null)
        {
            return tracked;
        }

        var level = await Db.StockLevels.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.BranchId == branchId && l.CatalogItemId == catalogItemId, cancellationToken);
        if (level is null)
        {
            level = new StockLevel { TenantId = tenantId, BranchId = branchId, CatalogItemId = catalogItemId, UpdatedAt = time.GetUtcNow() };
            Db.StockLevels.Add(level);
        }

        return level;
    }

    private async Task RequireProductAsync(Guid tenantId, Guid catalogItemId, CancellationToken cancellationToken)
    {
        if (!await Db.CatalogItems.AnyAsync(c => c.TenantId == tenantId && c.Id == catalogItemId && c.Kind == CatalogKind.Product, cancellationToken))
        {
            throw new NotFoundException();
        }
    }

    private async Task<PurchaseOrder> RequirePurchaseAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var order = await Db.PurchaseOrders.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == orderId, cancellationToken) ?? throw new NotFoundException();
        if (_caller.BranchId is { } only && order.BranchId != only)
        {
            throw new NotFoundException();
        }

        return order;
    }

    private async Task<PurchaseOrderView> PurchaseViewAsync(Guid tenantId, PurchaseOrder order, CancellationToken cancellationToken)
    {
        var lines = await Db.PurchaseOrderLines.AsNoTracking().Where(l => l.TenantId == tenantId && l.PurchaseOrderId == order.Id).ToListAsync(cancellationToken);
        var ids = lines.Select(l => l.CatalogItemId).ToList();
        var names = await Db.CatalogItems.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var supplier = order.SupplierId is { } sid ? await Db.Suppliers.AsNoTracking().Where(s => s.Id == sid).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken) : null;
        return new PurchaseOrderView(order, lines.Select(l => (l, names.GetValueOrDefault(l.CatalogItemId, "?"))).ToList(), supplier);
    }

    private async Task<int> NextNumberAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"purchase:{tenantId:N}")), 0);
        await Db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        return (await Db.PurchaseOrders.Where(p => p.TenantId == tenantId).MaxAsync(p => (int?)p.Number, cancellationToken) ?? 0) + 1;
    }

    private void Audit(Guid tenantId, string action, string target) =>
        Db.AuditLogs.Add(new AuditLog { TenantId = tenantId, UserId = _caller.UserId, Action = action, Target = target });
}
