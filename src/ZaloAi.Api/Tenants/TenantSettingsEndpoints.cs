using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Tenants;

internal static class TenantSettingsEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tenant").WithTags("Tenant");

        group.MapGet("/settings", GetAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", UpdateAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<UpdateTenantSettingsRequest>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    // tenantId luôn lấy từ ITenantContext (cookie), không có trong route/body.
    private static async Task<Ok<TenantSettingsResponse>> GetAsync(
        ITenantContext tenant,
        TenantRepository tenants,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var entity = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        return TypedResults.Ok(TenantSettingsResponse.From(entity));
    }

    private static async Task<Ok<TenantSettingsResponse>> UpdateAsync(
        UpdateTenantSettingsRequest request,
        ITenantContext tenant,
        TenantRepository tenants,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var entity = await tenants.GetAsync(tenantId, cancellationToken) ?? throw new NotFoundException();

        entity.Name = request.Name.Trim();
        entity.IndustrySlug = request.IndustrySlug;
        entity.BotName = request.BotName.Trim();
        entity.BotPronoun = request.BotPronoun.Trim();
        entity.PrivacyUrl = string.IsNullOrWhiteSpace(request.PrivacyUrl) ? null : request.PrivacyUrl.Trim();

        audit.Add(tenantId, tenant.UserId, "tenant.settings_updated", tenantId.ToString());
        await tenants.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(TenantSettingsResponse.From(entity));
    }
}
