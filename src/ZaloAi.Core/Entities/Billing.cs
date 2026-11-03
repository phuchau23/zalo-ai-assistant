namespace ZaloAi.Core.Entities;

public enum InvoiceStatus
{
    /// <summary>Chưa thu tiền (nháp / đang thanh toán).</summary>
    Open = 0,

    /// <summary>Thu một phần (còn nợ).</summary>
    Partial = 1,

    Paid = 2,

    /// <summary>Hủy hóa đơn (ghi lý do, giữ dòng để đối soát).</summary>
    Void = 3,

    /// <summary>Đã hoàn tiền (một phần hoặc toàn bộ).</summary>
    Refunded = 4,
}

public enum InvoiceLineKind
{
    Service = 0,
    Package = 1,
    Product = 2,

    /// <summary>Dùng buổi trong gói đã mua: giá 0, ghi nhận doanh thu từ gói.</summary>
    Redemption = 3,

    /// <summary>Dòng tự nhập (phụ thu, phí...).</summary>
    Custom = 4,
}

public enum PaymentMethod
{
    Cash = 0,
    Transfer = 1,
    Card = 2,

    /// <summary>Trừ thẻ trả trước (ví) của khách.</summary>
    Wallet = 3,

    /// <summary>Hoàn tiền (số âm).</summary>
    Refund = 4,
}

public enum CustomerPackageStatus
{
    Active = 0,
    Paused = 1,
    Expired = 2,
    Exhausted = 3,
    Transferred = 4,
    Cancelled = 5,
}

public enum WalletTransactionKind
{
    Topup = 0,
    Bonus = 1,
    Spend = 2,
    Refund = 3,
    Adjust = 4,
}

public enum VoucherKind
{
    Percent = 0,
    Amount = 1,
}

public enum RevenueKind
{
    Service = 0,

    /// <summary>Doanh thu gói ghi nhận theo buổi dùng (= giá gói / số buổi).</summary>
    PackageRecognized = 1,

    Product = 2,
    Custom = 3,

    /// <summary>Hoàn tiền (âm).</summary>
    Refund = 4,
}

/// <summary>
/// Hóa đơn / phiếu thu nội bộ (CRM C3) — KHÔNG phải hóa đơn điện tử theo luật (cần nhà cung cấp được cấp phép, để sau).
/// Số tiền lưu rõ (cần cộng dồn trong SQL); ghi chú mã hóa. Số hóa đơn tăng dần theo tenant.
/// </summary>
public sealed class Invoice : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    /// <summary>Số thứ tự trong tenant (hiển thị "HD-000123").</summary>
    public int Number { get; set; }

    public Guid? ContactId { get; set; }

    public Guid? AppointmentId { get; set; }

    public Guid? CashierUserId { get; set; }

    public Guid? CashShiftId { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Open;

    /// <summary>Tổng dòng trước giảm giá hóa đơn.</summary>
    public long Subtotal { get; set; }

    /// <summary>Giảm giá cấp hóa đơn (VNĐ).</summary>
    public long DiscountAmount { get; set; }

    public string? DiscountReason { get; set; }

    /// <summary>Giảm vượt ngưỡng cần chủ duyệt → người duyệt.</summary>
    public Guid? DiscountApprovedBy { get; set; }

    public Guid? VoucherId { get; set; }

    public long VoucherAmount { get; set; }

    public long Total { get; set; }

    public long PaidAmount { get; set; }

    public long RefundedAmount { get; set; }

    public string? NoteEnc { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedBy { get; set; }

    public string? VoidReason { get; set; }

    /// <summary>Đã gửi phiếu thu cho khách qua kênh (Zalo).</summary>
    public DateTimeOffset? ReceiptSentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public long Due => Math.Max(0, Total - PaidAmount + RefundedAmount);
}

public sealed class InvoiceLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid InvoiceId { get; set; }

    public InvoiceLineKind Kind { get; set; }

    public Guid? CatalogItemId { get; set; }

    public required string Description { get; set; }

    public int Quantity { get; set; } = 1;

    public long UnitPrice { get; set; }

    /// <summary>Giảm giá dòng (VNĐ).</summary>
    public long Discount { get; set; }

    public long LineTotal { get; set; }

    /// <summary>Nhân viên thực hiện (chia doanh số, hoa hồng C5).</summary>
    public Guid? PerformerStaffId { get; set; }

    /// <summary>Dòng dùng buổi gói → gói nào.</summary>
    public Guid? CustomerPackageId { get; set; }

    public int Position { get; set; }
}

public sealed class Payment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid InvoiceId { get; set; }

    public PaymentMethod Method { get; set; }

    /// <summary>VNĐ; hoàn tiền ghi số âm với <see cref="PaymentMethod.Refund"/>.</summary>
    public long Amount { get; set; }

    /// <summary>Mã giao dịch / số tham chiếu chuyển khoản.</summary>
    public string? Reference { get; set; }

    public Guid? ReceivedByUserId { get; set; }

    public Guid? CashShiftId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Gói liệu trình khách đã mua: tổng buổi, đã dùng, hạn. Mỗi lần đến trừ 1 buổi (<see cref="PackageRedemption"/>).</summary>
public sealed class CustomerPackage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid ContactId { get; set; }

    public Guid CatalogItemId { get; set; }

    public required string Name { get; set; }

    public Guid? InvoiceId { get; set; }

    public int TotalSessions { get; set; }

    public int UsedSessions { get; set; }

    /// <summary>Giá đã trả cho gói (để ghi nhận doanh thu mỗi buổi = giá / tổng buổi).</summary>
    public long PricePaid { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public CustomerPackageStatus Status { get; set; } = CustomerPackageStatus.Active;

    public Guid? TransferredToContactId { get; set; }

    public Guid? TransferredFromPackageId { get; set; }

    /// <summary>Đã nhắn khách gói sắp hết (C7) — không nhắn lại.</summary>
    public DateTimeOffset? LowBalanceNotifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Remaining => Math.Max(0, TotalSessions - UsedSessions);
}

public sealed class PackageRedemption : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid CustomerPackageId { get; set; }

    public Guid? AppointmentId { get; set; }

    public Guid? InvoiceLineId { get; set; }

    public Guid? StaffMemberId { get; set; }

    public Guid? ByUserId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Hoàn lại buổi (hủy hóa đơn / nhầm).</summary>
    public DateTimeOffset? RevertedAt { get; set; }
}

/// <summary>Thẻ trả trước (ví) của khách: nạp X dùng X + thưởng; trừ dần khi thanh toán.</summary>
public sealed class Wallet : ITenantOwned
{
    public Guid ContactId { get; set; }

    public Guid TenantId { get; set; }

    public long Balance { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WalletTransaction : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid ContactId { get; set; }

    public WalletTransactionKind Kind { get; set; }

    /// <summary>Dương = cộng, âm = trừ.</summary>
    public long Amount { get; set; }

    public long BalanceAfter { get; set; }

    public Guid? InvoiceId { get; set; }

    public string? Note { get; set; }

    public Guid? ByUserId { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class Voucher : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    /// <summary>Mã khách nhập (in hoa, không trùng trong tenant).</summary>
    public required string Code { get; set; }

    public VoucherKind Kind { get; set; }

    /// <summary>% (1–100) hoặc VNĐ.</summary>
    public long Value { get; set; }

    /// <summary>Giảm tối đa (VNĐ) với loại %; null = không giới hạn.</summary>
    public long? MaxDiscount { get; set; }

    public long MinTotal { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidTo { get; set; }

    /// <summary>null = không giới hạn.</summary>
    public int? MaxUses { get; set; }

    public int UsedCount { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Ca thu ngân: mở ca với tiền đầu ca, chốt ca đối soát tiền mặt (thu − hoàn) với tiền đếm thực tế.</summary>
public sealed class CashShift : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid OpenedByUserId { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public long OpeningCash { get; set; }

    public Guid? ClosedByUserId { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Tiền mặt hệ thống tính khi chốt = đầu ca + thu tiền mặt − hoàn tiền mặt.</summary>
    public long? ExpectedCash { get; set; }

    public long? CountedCash { get; set; }

    public string? Note { get; set; }
}

/// <summary>Doanh thu ghi nhận (báo cáo C6): dịch vụ/sản phẩm khi thu tiền; gói theo từng buổi dùng; hoàn tiền âm.</summary>
public sealed class RevenueEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    /// <summary>Ngày địa phương của DN.</summary>
    public DateOnly Date { get; set; }

    public RevenueKind Kind { get; set; }

    public long Amount { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? InvoiceLineId { get; set; }

    public Guid? RedemptionId { get; set; }

    public Guid? CatalogItemId { get; set; }

    public Guid? StaffMemberId { get; set; }

    public Guid? ContactId { get; set; }

    /// <summary>Nguồn khách của hóa đơn (bot | staff | walk_in | online) để báo cáo "doanh thu bot mang về".</summary>
    public string? Source { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>Tài khoản ngân hàng nhận chuyển khoản (VietQR). Số tài khoản mã hóa.</summary>
public sealed class TenantBankAccount : ITenantOwned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TenantId { get; set; }

    public Guid? BranchId { get; set; }

    /// <summary>Mã BIN ngân hàng (6 số theo NAPAS, ví dụ 970436 Vietcombank).</summary>
    public required string BankBin { get; set; }

    public required string BankName { get; set; }

    public required string AccountNoEnc { get; set; }

    public required string AccountName { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
