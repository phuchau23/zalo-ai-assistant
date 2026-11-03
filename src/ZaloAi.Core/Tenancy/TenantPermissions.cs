namespace ZaloAi.Core.Tenancy;

/// <summary>
/// Công tắc quyền chi tiết cho NHÂN VIÊN (docs/CRM-PLAN.md C1). Chủ DN luôn có mọi quyền; nhân viên mặc định không có quyền
/// nào dưới đây (chỉ việc cơ bản: xem lịch/khách của chi nhánh mình, tạo lịch, tạo hóa đơn, xem doanh số của mình).
/// Lưu dạng chuỗi trong memberships.permissions để thêm quyền mới không cần migration.
/// </summary>
public static class TenantPermissions
{
    public const string ViewCompanyRevenue = "view_company_revenue";
    public const string ViewAllBranches = "view_all_branches";
    public const string ManageCatalog = "manage_catalog";
    public const string ManageStaff = "manage_staff";
    public const string ManageBranches = "manage_branches";
    public const string ApplyDiscountOverLimit = "apply_discount_over_limit";
    public const string VoidInvoice = "void_invoice";
    public const string Refund = "refund";
    public const string ExportData = "export_data";
    public const string DeleteCustomer = "delete_customer";
    public const string ManageInventory = "manage_inventory";
    public const string ViewReports = "view_reports";

    public static IReadOnlyList<string> All { get; } =
    [
        ViewCompanyRevenue, ViewAllBranches, ManageCatalog, ManageStaff, ManageBranches, ApplyDiscountOverLimit,
        VoidInvoice, Refund, ExportData, DeleteCustomer, ManageInventory, ViewReports,
    ];

    public static bool IsKnown(string? permission) => permission is not null && All.Contains(permission, StringComparer.Ordinal);
}
