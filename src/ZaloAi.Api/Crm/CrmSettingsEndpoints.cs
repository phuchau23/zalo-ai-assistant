using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Crm;

public sealed record CrmSettingsResponse(string TimeZone, int SlotMinutes, int DiscountApprovalPercent, int DefaultBufferMinutes, IReadOnlyList<string> AllPermissions)
{
    internal static CrmSettingsResponse From(CrmSettings s) =>
        new(s.TimeZone, s.SlotMinutes, s.DiscountApprovalPercent, s.DefaultBufferMinutes, TenantPermissions.All);
}

public sealed record UpdateCrmSettingsRequest(string TimeZone, int SlotMinutes, int DiscountApprovalPercent, int DefaultBufferMinutes);

internal sealed class UpdateCrmSettingsRequestValidator : AbstractValidator<UpdateCrmSettingsRequest>
{
    public UpdateCrmSettingsRequestValidator()
    {
        RuleFor(x => x.TimeZone).NotEmpty().MaximumLength(64).Must(tz => TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithMessage("Múi giờ không hợp lệ.");
        RuleFor(x => x.SlotMinutes).Must(m => m is 15 or 30 or 60).WithMessage("Bước lịch 15, 30 hoặc 60 phút.");
        RuleFor(x => x.DiscountApprovalPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.DefaultBufferMinutes).InclusiveBetween(0, 120);
    }
}

/// <summary>Cài đặt CRM (múi giờ, bước lịch, ngưỡng giảm giá). Xem: mọi thành viên; sửa: chủ.</summary>
internal static class CrmSettingsEndpoints
{
    public static IEndpointRouteBuilder MapCrmSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tenant/crm-settings").WithTags("CRM");
        group.MapGet("", GetAsync).RequireTenantRole(TenantRole.Staff);
        group.MapPut("", UpdateAsync).RequireTenantRole(TenantRole.Owner).Validate<UpdateCrmSettingsRequest>();
        return app;
    }

    private static async Task<Ok<CrmSettingsResponse>> GetAsync(ITenantContext tenant, CrmSettingsRepository settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(CrmSettingsResponse.From(await settings.GetAsync(tenant.RequireTenantId(), cancellationToken)));

    private static async Task<Ok<CrmSettingsResponse>> UpdateAsync(
        UpdateCrmSettingsRequest request, ITenantContext tenant, CrmSettingsRepository settings, AuditLogRepository audit, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var s = await settings.GetOrCreateAsync(tenantId, cancellationToken);
        s.TimeZone = request.TimeZone.Trim();
        s.SlotMinutes = request.SlotMinutes;
        s.DiscountApprovalPercent = request.DiscountApprovalPercent;
        s.DefaultBufferMinutes = request.DefaultBufferMinutes;
        audit.Add(tenantId, tenant.UserId, "tenant.crm_settings_updated", tenantId.ToString());
        await settings.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(CrmSettingsResponse.From(s));
    }
}
