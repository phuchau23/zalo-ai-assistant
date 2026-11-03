namespace ZaloAi.Core.Entities;

public enum BranchStatus
{
    Active = 0,
    Inactive = 1,
}

/// <summary>
/// Chi nhánh / cơ sở của doanh nghiệp (CRM C1). Mỗi tenant có ít nhất một chi nhánh mặc định (tự tạo). Lịch hẹn, hóa đơn, kho
/// đều gắn chi nhánh; nhân viên không có quyền xem mọi chi nhánh chỉ thấy chi nhánh của mình.
/// </summary>
public sealed class Branch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    /// <summary>Mã ngắn, duy nhất trong tenant (ví dụ "Q12"); nối với cột "Chi nhánh" của kho kiến thức.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public bool IsDefault { get; set; }

    public BranchStatus Status { get; set; } = BranchStatus.Active;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Khung giờ mở cửa theo thứ (0 = Chủ nhật … 6 = Thứ bảy, giống <see cref="DayOfWeek"/>). Một ngày có thể nhiều khung.</summary>
public sealed class BranchHour : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly OpenTime { get; set; }

    public TimeOnly CloseTime { get; set; }
}

/// <summary>Ngày nghỉ (lễ, Tết, sửa chữa) của một chi nhánh — không nhận lịch hẹn.</summary>
public sealed class BranchClosure : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public DateOnly Date { get; set; }

    public string? Reason { get; set; }
}
