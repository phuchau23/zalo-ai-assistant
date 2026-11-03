namespace ZaloAi.Core.Entities;

public enum CommissionKind
{
    /// <summary>% trên doanh thu dòng.</summary>
    Percent = 0,

    /// <summary>Số tiền cố định mỗi lượt/buổi.</summary>
    Fixed = 1,
}

/// <summary>Quy tắc áp cho loại nào: mọi mục, dịch vụ, gói (tính theo buổi dùng), sản phẩm, hoặc một mục cụ thể.</summary>
public enum CommissionScope
{
    All = 0,
    Service = 1,
    Package = 2,
    Product = 3,
    Item = 4,
}

/// <summary>
/// Quy tắc hoa hồng (CRM C5). Ưu tiên: mục cụ thể của nhân viên → mục cụ thể chung → loại của nhân viên → loại chung → mọi mục của
/// nhân viên → mọi mục chung. StaffMemberId null = áp cho mọi nhân viên.
/// </summary>
public sealed class CommissionRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid? StaffMemberId { get; set; }

    public CommissionScope Scope { get; set; }

    public Guid? CatalogItemId { get; set; }

    public CommissionKind Kind { get; set; }

    /// <summary>% (0–100) hoặc VNĐ.</summary>
    public long Value { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Một khoản hoa hồng phát sinh từ một dòng hóa đơn (thu đủ) hoặc một buổi dùng gói. Hủy hóa đơn → RevertedAt.</summary>
public sealed class CommissionEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? InvoiceLineId { get; set; }

    public Guid? RedemptionId { get; set; }

    public Guid? RuleId { get; set; }

    /// <summary>Doanh thu làm cơ sở (VNĐ).</summary>
    public long Basis { get; set; }

    public long Amount { get; set; }

    /// <summary>Kỳ "yyyy-MM" theo giờ DN (bảng hoa hồng tháng).</summary>
    public required string Period { get; set; }

    public DateTimeOffset At { get; set; }

    public DateTimeOffset? RevertedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>Chấm công: vào ca / ra ca (nhân viên tự bấm trên điện thoại hoặc quản lý ghi).</summary>
public sealed class Attendance : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }

    public Guid BranchId { get; set; }

    public DateTimeOffset ClockInAt { get; set; }

    public DateTimeOffset? ClockOutAt { get; set; }

    /// <summary>self (tự bấm) | manager (quản lý ghi/sửa)</summary>
    public string Source { get; set; } = "self";

    public string? Note { get; set; }

    public Guid? ByUserId { get; set; }
}
