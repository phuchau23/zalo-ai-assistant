using Hangfire;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Crm;

/// <summary>
/// Đồng bộ hai chiều danh mục bán (<see cref="CatalogItem"/>) ↔ kho kiến thức (<see cref="KnowledgeItem"/> loại Dịch vụ / Gói / Sản phẩm)
/// theo MÃ. Danh mục là nguồn sự thật về giá/thời lượng/số buổi; kho kiến thức là bản để bot đọc (mô tả, phù hợp với...).
/// - Nhập file kiến thức → tạo/cập nhật mục danh mục cùng mã; mục bị xóa khỏi file → danh mục ngừng bán (giữ lịch sử).
/// - Sửa giá/tên/thời lượng ở Danh mục → ghi ngược vào mục kiến thức, đánh chỉ mục lại (giữ cờ "đã duyệt chuyên môn").
/// Không ghi gì khi người gọi chưa SaveChanges — các hàm chỉ thêm vào change tracker.
/// </summary>
public sealed class CatalogKnowledgeSync(AppDbContext db, ITenantContext tenantContext, ChunkRepository chunks, IBackgroundJobClient jobs)
    : TenantScopedRepository(db, tenantContext)
{
    public static CatalogKind? KindOf(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Service => CatalogKind.Service,
        KnowledgeKind.Package => CatalogKind.Package,
        KnowledgeKind.Product => CatalogKind.Product,
        _ => null,
    };

    public static KnowledgeKind KindOf(CatalogKind kind) => kind switch
    {
        CatalogKind.Service => KnowledgeKind.Service,
        CatalogKind.Package => KnowledgeKind.Package,
        _ => KnowledgeKind.Product,
    };

    /// <summary>Kho kiến thức → danh mục (gọi trong transaction áp dụng nhập file, trước SaveChanges).</summary>
    public async Task SyncFromKnowledgeAsync(
        Guid tenantId,
        IReadOnlyList<KnowledgeItem> changed,
        IReadOnlyList<string> removedCodes,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(removedCodes);

        var relevant = changed.Where(i => KindOf(i.Kind) is not null).ToList();
        var codes = relevant.Select(i => i.Code).Concat(removedCodes).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count == 0)
        {
            return;
        }

        var existing = await Db.CatalogItems.Where(c => c.TenantId == tenantId && codes.Contains(c.Code))
            .ToDictionaryAsync(c => c.Code, StringComparer.Ordinal, cancellationToken);
        var nextSort = await Db.CatalogItems.CountAsync(c => c.TenantId == tenantId, cancellationToken);

        foreach (var item in relevant)
        {
            if (existing.TryGetValue(item.Code, out var catalog))
            {
                ApplyKnowledge(catalog, item);
            }
            else
            {
                catalog = new CatalogItem { TenantId = tenantId, Code = item.Code, Name = "", Kind = KindOf(item.Kind)!.Value, SortOrder = nextSort++ };
                ApplyKnowledge(catalog, item);
                Db.CatalogItems.Add(catalog);
            }
        }

        foreach (var code in removedCodes)
        {
            if (existing.TryGetValue(code, out var catalog))
            {
                catalog.IsActive = false;
                catalog.KnowledgeItemId = null;
            }
        }
    }

    /// <summary>Tạo mục danh mục cho những mục kiến thức chưa có (tenant cũ, hoặc dữ liệu nhập trước C1). Idempotent, rẻ.</summary>
    public async Task EnsureSeededAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var kinds = new[] { KnowledgeKind.Service, KnowledgeKind.Package, KnowledgeKind.Product };
        var missing = await Db.KnowledgeItems.AsNoTracking()
            .Where(k => k.TenantId == tenantId && kinds.Contains(k.Kind)
                        && !Db.CatalogItems.Any(c => c.TenantId == tenantId && c.Code == k.Code))
            .ToListAsync(cancellationToken);
        if (missing.Count == 0)
        {
            return;
        }

        await SyncFromKnowledgeAsync(tenantId, missing, [], cancellationToken);
        await Db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Danh mục → kho kiến thức: cập nhật mục cùng mã (hoặc tạo mới), xóa đoạn cũ, xếp lịch đánh chỉ mục lại.</summary>
    public async Task PushToKnowledgeAsync(Guid tenantId, CatalogItem catalog, Guid? userId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        ArgumentNullException.ThrowIfNull(catalog);
        var kind = KindOf(catalog.Kind);
        var item = await Db.KnowledgeItems.FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Code == catalog.Code, cancellationToken);
        var fields = item is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(KnowledgeItemMapper.FromItem(item).Fields, StringComparer.Ordinal);

        fields["name"] = catalog.Name;
        Set(fields, "group", catalog.GroupName);
        Set(fields, "price", catalog.Price);
        switch (catalog.Kind)
        {
            case CatalogKind.Service:
                Set(fields, "durationMinutes", catalog.DurationMinutes);
                Set(fields, "priceUnit", catalog.Unit);
                Set(fields, "priceNote", catalog.PriceNote);
                break;
            case CatalogKind.Package:
                Set(fields, "sessions", catalog.SessionCount);
                Set(fields, "minutesPerSession", catalog.DurationMinutes);
                Set(fields, "priceNote", catalog.PriceNote);
                break;
            default:
                Set(fields, "unit", catalog.Unit);
                Set(fields, "sku", catalog.Sku);
                break;
        }

        var entry = new KnowledgeEntry(kind, catalog.Code, fields);
        if (item is null)
        {
            item = KnowledgeItemMapper.ToItem(entry, userId);
            item.TenantId = tenantId;
            item.Id = Guid.CreateVersion7();
            Db.KnowledgeItems.Add(item);
        }
        else
        {
            if (item.Kind != kind)
            {
                throw new ZaloAi.Core.Errors.ConflictException($"Mã {catalog.Code} đang là mục kiến thức loại khác.", "code_kind_mismatch");
            }

            var reviewed = item.MedicallyReviewed; // chỉ đổi giá/tên/thời lượng: không bỏ duyệt chuyên môn
            KnowledgeItemMapper.Apply(item, entry, userId);
            item.MedicallyReviewed = reviewed;
            await chunks.DeleteForItemAsync(tenantId, item.Id, cancellationToken);
        }

        catalog.KnowledgeItemId = item.Id;
        chunks.AddRange(tenantId, [KnowledgeImportService.BuildChunk(item)]);
        jobs.Enqueue<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));
    }

    private static void ApplyKnowledge(CatalogItem catalog, KnowledgeItem item)
    {
        var entry = KnowledgeItemMapper.FromItem(item);
        catalog.Kind = KindOf(item.Kind)!.Value;
        catalog.KnowledgeItemId = item.Id;
        catalog.Name = entry.Title;
        catalog.GroupName = entry.Text("group");
        catalog.Price = entry.Number("price");
        catalog.IsActive = true;
        switch (catalog.Kind)
        {
            case CatalogKind.Service:
                catalog.DurationMinutes = (int?)entry.Number("durationMinutes");
                catalog.Unit = entry.Text("priceUnit") ?? "lượt";
                catalog.PriceNote = entry.Text("priceNote");
                break;
            case CatalogKind.Package:
                catalog.SessionCount = (int?)entry.Number("sessions");
                catalog.DurationMinutes = (int?)entry.Number("minutesPerSession");
                catalog.Unit = "gói";
                catalog.PriceNote = entry.Text("priceNote");
                break;
            default:
                catalog.Unit = entry.Text("unit");
                catalog.Sku = entry.Text("sku");
                break;
        }
    }

    private static void Set(Dictionary<string, object> fields, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            fields.Remove(name);
        }
        else
        {
            fields[name] = value.Trim();
        }
    }

    private static void Set(Dictionary<string, object> fields, string name, long? value)
    {
        if (value is null)
        {
            fields.Remove(name);
        }
        else
        {
            fields[name] = value.Value;
        }
    }

    private static void Set(Dictionary<string, object> fields, string name, int? value) => Set(fields, name, (long?)value);
}
