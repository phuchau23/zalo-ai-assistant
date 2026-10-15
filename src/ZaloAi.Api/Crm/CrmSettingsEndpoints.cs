using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Crm;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Crm;

public sealed record CrmSettingsResponse(
    string TimeZone,
    int SlotMinutes,
    int DiscountApprovalPercent,
    int DefaultBufferMinutes,
    bool BotBookingEnabled,
    int BotMaxPartySize,
    int BookingLeadMinutes,
    int MaxBookingDaysAhead,
    int CancelLeadHours,
    int Reminder1Hours,
    int Reminder2Hours,
    IReadOnlyList<string> AllPermissions)
{
    internal static CrmSettingsResponse From(CrmSettings s) => new(
        s.TimeZone, s.SlotMinutes, s.DiscountApprovalPercent, s.DefaultBufferMinutes, s.BotBookingEnabled, s.BotMaxPartySize, s.BookingLeadMinutes,
        s.MaxBookingDaysAhead, s.CancelLeadHours, s.Reminder1Hours, s.Reminder2Hours, TenantPermissions.All);
}

/// <param name="Reminder1Hours">0 = tắt.</param>
/// <param name="Reminder2Hours">0 = tắt.</param>
public sealed record UpdateCrmSettingsRequest(
    string TimeZone,
    int SlotMinutes,
    int DiscountApprovalPercent,
    int DefaultBufferMinutes,
    bool BotBookingEnabled = true,
    int BotMaxPartySize = 2,
    int BookingLeadMinutes = 60,
    int MaxBookingDaysAhead = 60,
    int CancelLeadHours = 2,
    int Reminder1Hours = 24,
    int Reminder2Hours = 2);

internal sealed class UpdateCrmSettingsRequestValidator : AbstractValidator<UpdateCrmSettingsRequest>
{
    public UpdateCrmSettingsRequestValidator()
    {
        RuleFor(x => x.TimeZone).NotEmpty().MaximumLength(64).Must(tz => TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithMessage("Múi giờ không hợp lệ.");
        RuleFor(x => x.SlotMinutes).Must(m => m is 15 or 30 or 60).WithMessage("Bước lịch 15, 30 hoặc 60 phút.");
        RuleFor(x => x.DiscountApprovalPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.DefaultBufferMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.BotMaxPartySize).InclusiveBetween(1, 20);
        RuleFor(x => x.BookingLeadMinutes).InclusiveBetween(0, 1440);
        RuleFor(x => x.MaxBookingDaysAhead).InclusiveBetween(1, 365);
        RuleFor(x => x.CancelLeadHours).InclusiveBetween(0, 168);
        RuleFor(x => x.Reminder1Hours).InclusiveBetween(0, 168);
        RuleFor(x => x.Reminder2Hours).InclusiveBetween(0, 48);
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
        s.BotBookingEnabled = request.BotBookingEnabled;
        s.BotMaxPartySize = request.BotMaxPartySize;
        s.BookingLeadMinutes = request.BookingLeadMinutes;
        s.MaxBookingDaysAhead = request.MaxBookingDaysAhead;
        s.CancelLeadHours = request.CancelLeadHours;
        s.Reminder1Hours = request.Reminder1Hours;
        s.Reminder2Hours = request.Reminder2Hours;
        audit.Add(tenantId, tenant.UserId, "tenant.crm_settings_updated", tenantId.ToString());
        await settings.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(CrmSettingsResponse.From(s));
    }
}
