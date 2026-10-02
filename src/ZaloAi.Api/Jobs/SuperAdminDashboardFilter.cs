using Hangfire.Dashboard;
using ZaloAi.Core.Tenancy;

namespace ZaloAi.Api.Jobs;

/// <summary>
/// Dashboard Hangfire chỉ cho super admin: nó hiện job của mọi tenant (kể cả tham số) và cho chạy lại/xóa job.
/// ITenantContext đã được TenantContextMiddleware set từ cookie và kiểm lại cờ super admin trong DB.
/// </summary>
internal sealed class SuperAdminDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var tenant = context.GetHttpContext().RequestServices.GetRequiredService<ITenantContext>();
        return tenant.IsSuperAdmin;
    }
}
