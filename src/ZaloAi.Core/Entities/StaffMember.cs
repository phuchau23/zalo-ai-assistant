namespace ZaloAi.Core.Entities;

public enum StaffStatus
{
    Active = 0,
    Inactive = 1,
}

public enum ShiftSource
{
    /// <summary>Sinh từ ca cố định hằng tuần.</summary>
    Template = 0,

    /// <summary>Nhập tay cho ngày cụ thể (ghi đè ca cố định).</summary>
    Manual = 1,
}

/// <summary>
/// Nhân viên / kỹ thuật viên (CRM C1) — người được xếp lịch và tính doanh số. Khác với <see cref="Membership"/> (tài khoản đăng nhập):
/// kỹ thuật viên có thể không có tài khoản; <see cref="UserId"/> gắn khi họ cũng đăng nhập. Tên hiển thị công khai nội bộ; SĐT mã hóa.
/// </summary>
public sealed class StaffMember : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid? UserId { get; set; }

    public Guid BranchId { get; set; }

    public required string DisplayName { get; set; }

    public string? Title { get; set; }

    public string? PhoneEnc { get; set; }

    /// <summary>Màu ô lịch, dạng "#RRGGBB".</summary>
    public string Color { get; set; } = "#2563eb";

    /// <summary>Có nhận lịch hẹn không (lễ tân, quản lý: false).</summary>
    public bool Bookable { get; set; } = true;

    public StaffStatus Status { get; set; } = StaffStatus.Active;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Nhân viên làm được dịch vụ nào (lịch chỉ xếp dịch vụ cho người làm được). Không có dòng nào = mọi dịch vụ.</summary>
public sealed class StaffSkill : ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }

    public Guid CatalogItemId { get; set; }
}

/// <summary>Ca cố định hằng tuần (thứ 0–6 như <see cref="DayOfWeek"/>).</summary>
public sealed class StaffShiftTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }

    public DayOfWeek DayOfWeek { get; set; }

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }
}

/// <summary>Ca làm của một ngày cụ thể. Có dòng Manual cho ngày đó → bỏ qua ca cố định của ngày đó.</summary>
public sealed class StaffShift : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }

    public Guid BranchId { get; set; }

    public DateOnly Date { get; set; }

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }

    public ShiftSource Source { get; set; } = ShiftSource.Manual;
}

/// <summary>Nghỉ phép / bận (không nhận lịch trong khoảng này).</summary>
public sealed class StaffTimeOff : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid StaffMemberId { get; set; }

    public DateTimeOffset FromAt { get; set; }

    public DateTimeOffset ToAt { get; set; }

    public string? Reason { get; set; }
}
