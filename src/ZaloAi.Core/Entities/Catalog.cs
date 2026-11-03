namespace ZaloAi.Core.Entities;

public enum CatalogKind
{
    /// <summary>Dịch vụ lẻ (có thời lượng, đặt lịch được).</summary>
    Service = 0,

    /// <summary>Gói liệu trình: bán trước N buổi, mỗi lần đến trừ 1 buổi.</summary>
    Package = 1,

    /// <summary>Sản phẩm bán lẻ (có thể theo dõi tồn kho).</summary>
    Product = 2,
}

/// <summary>
/// Danh mục bán hàng (CRM C1): nguồn sự thật cho giá, thời lượng, số buổi. Đồng bộ hai chiều với kho kiến thức
/// (<see cref="KnowledgeItem"/> cùng mã, loại Dịch vụ / Gói / Sản phẩm) để bot tư vấn đúng giá đang bán.
/// Thông tin công khai của DN → không mã hóa.
/// </summary>
public sealed class CatalogItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public CatalogKind Kind { get; set; }

    /// <summary>Mã theo quy tắc kho kiến thức (chữ in hoa, số, gạch ngang), duy nhất trong tenant.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? GroupName { get; set; }

    /// <summary>VNĐ; null = liên hệ báo giá (bot không được nêu số).</summary>
    public long? Price { get; set; }

    public string? PriceNote { get; set; }

    /// <summary>Dịch vụ: phút làm; gói: phút mỗi buổi.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Phút dọn dẹp/nghỉ sau dịch vụ trước khi nhận khách kế (tính chỗ trống lịch).</summary>
    public int BufferMinutes { get; set; }

    /// <summary>Gói: tổng số buổi.</summary>
    public int? SessionCount { get; set; }

    /// <summary>Gói: hạn dùng tính từ ngày mua (ngày); null = không hết hạn.</summary>
    public int? ValidityDays { get; set; }

    /// <summary>Sản phẩm: mã hàng nội bộ / mã vạch.</summary>
    public string? Sku { get; set; }

    /// <summary>Đơn vị bán (lượt, buổi, chai, hộp...).</summary>
    public string? Unit { get; set; }

    /// <summary>Sản phẩm: giá vốn (VNĐ) để tính lãi gộp.</summary>
    public long? CostPrice { get; set; }

    /// <summary>Sản phẩm: có theo dõi tồn kho (C4).</summary>
    public bool TrackInventory { get; set; }

    /// <summary>Mục kho kiến thức cùng mã (để bot đọc); null khi chưa đồng bộ.</summary>
    public Guid? KnowledgeItemId { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Mục chỉ bán ở một số chi nhánh. Không có dòng nào = mọi chi nhánh.</summary>
public sealed class CatalogItemBranch : ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CatalogItemId { get; set; }

    public Guid BranchId { get; set; }
}

/// <summary>Gói gồm dịch vụ nào, mấy buổi (gói 10 buổi massage = 1 dòng; combo = nhiều dòng).</summary>
public sealed class PackageContent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid PackageId { get; set; }

    public Guid ServiceId { get; set; }

    public int SessionCount { get; set; }
}
