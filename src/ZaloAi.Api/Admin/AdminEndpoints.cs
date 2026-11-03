using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.IndustryTemplates;
using ZaloAi.Infrastructure.Admin;
using ZaloAi.Infrastructure.Dashboard;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Admin;

/// <param name="ConversationsPerMonth">null = không giới hạn.</param>
public sealed record PlanResponse(string Code, string Name, int? ConversationsPerMonth);

/// <param name="Status">active | suspended | cancelled</param>
/// <param name="SuspendedReason">manual (bạn khóa) | expired (tự khóa khi quá hạn thuê)</param>
/// <param name="Quota">Hạn mức hội thoại/tháng đang áp dụng (null = không giới hạn).</param>
/// <param name="QuotaOverride">Hạn mức riêng bạn đặt; null = theo gói.</param>
/// <param name="ConversationsThisMonth">Hội thoại thật (không tính Chat thử) từ đầu tháng giờ Việt Nam.</param>
public sealed record AdminTenantItem(
    Guid Id,
    string Name,
    string IndustrySlug,
    string IndustryName,
    string Plan,
    string PlanName,
    string Status,
    string? SuspendedReason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string? OwnerEmail,
    int MemberCount,
    int ConversationsThisMonth,
    int? Quota,
    int? QuotaOverride,
    decimal AiCostThisMonthUsd,
    decimal AiCostLastMonthUsd);

/// <param name="Role">owner | staff</param>
public sealed record AdminMember(Guid UserId, string Email, string Name, string Role);

/// <param name="Month">Ngày 1 của tháng (giờ Việt Nam).</param>
public sealed record AdminMonthlyCost(DateOnly Month, decimal CostUsd, int Calls);

/// <param name="MonthlyCosts">12 tháng gần nhất, đủ mọi tháng (tháng không có = 0), cũ trước.</param>
public sealed record AdminTenantDetail(AdminTenantItem Tenant, IReadOnlyList<AdminMember> Members, IReadOnlyList<AdminMonthlyCost> MonthlyCosts);

/// <param name="ExpiresAt">Hạn thuê (kèm múi giờ); null = không thời hạn.</param>
/// <param name="ConversationQuota">Hạn mức riêng; null = theo gói.</param>
public sealed record CreateTenantRequest(
    string Name,
    string IndustrySlug,
    string? BotName,
    string OwnerEmail,
    string OwnerName,
    string Plan,
    DateTimeOffset? ExpiresAt,
    int? ConversationQuota);

/// <param name="TemporaryPassword">Mật khẩu tạm của tài khoản chủ mới — chỉ hiện MỘT lần; null khi email đã có tài khoản.</param>
public sealed record CreateTenantResponse(Guid TenantId, string OwnerEmail, string? TemporaryPassword);

public sealed record UpdateSubscriptionRequest(string Plan, DateTimeOffset? ExpiresAt, int? ConversationQuota);

/// <param name="ConfirmName">Gõ đúng tên doanh nghiệp để xác nhận xóa.</param>
public sealed record PurgeTenantRequest(string ConfirmName);

internal sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithName("Tên doanh nghiệp");
        RuleFor(x => x.IndustrySlug).Must(IndustryCatalog.IsKnown).WithMessage("Ngành chưa có trong danh sách.");
        RuleFor(x => x.BotName).MaximumLength(50).WithName("Tên bot");
        RuleFor(x => x.OwnerEmail).NotEmpty().MaximumLength(320).EmailAddress().WithName("Email chủ doanh nghiệp");
        RuleFor(x => x.OwnerName).NotEmpty().MaximumLength(200).WithName("Tên chủ doanh nghiệp");
        RuleFor(x => x.Plan).NotEmpty().MaximumLength(30).WithName("Gói");
        RuleFor(x => x.ConversationQuota).InclusiveBetween(0, 1_000_000).WithName("Hạn mức");
    }
}

internal sealed class UpdateSubscriptionRequestValidator : AbstractValidator<UpdateSubscriptionRequest>
{
    public UpdateSubscriptionRequestValidator()
    {
        RuleFor(x => x.Plan).NotEmpty().MaximumLength(30).WithName("Gói");
        RuleFor(x => x.ConversationQuota).InclusiveBetween(0, 1_000_000).WithName("Hạn mức");
    }
}

internal sealed class PurgeTenantRequestValidator : AbstractValidator<PurgeTenantRequest>
{
    public PurgeTenantRequestValidator() => RuleFor(x => x.ConfirmName).NotEmpty().MaximumLength(200).WithName("Tên xác nhận");
}

/// <summary>
/// Trang super admin (CLAUDE.md M6 đợt B3 — chỉ chủ dự án): danh sách DN, gói, hạn, trạng thái, chi phí AI theo tháng; tạo DN;
/// khóa/mở; xuất toàn bộ dữ liệu và xóa DN khi ngừng thuê. Đọc tổng hợp qua <see cref="SuperAdminRepository"/>, ghi qua
/// <see cref="TenantAdminService"/> (đặt tenant context đúng DN, có audit log).
/// </summary>
internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").WithTags("Admin").RequireSuperAdmin();
        group.MapGet("/plans", ListPlans);
        group.MapGet("/tenants", ListAsync);
        group.MapGet("/tenants/{tenantId:guid}", GetAsync).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/tenants", CreateAsync).Validate<CreateTenantRequest>();
        group.MapPut("/tenants/{tenantId:guid}/subscription", UpdateSubscriptionAsync)
            .Validate<UpdateSubscriptionRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/tenants/{tenantId:guid}/suspend", SuspendAsync).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/tenants/{tenantId:guid}/resume", ResumeAsync).ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/tenants/{tenantId:guid}/export", ExportAsync)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/tenants/{tenantId:guid}/purge", PurgeAsync)
            .Validate<PurgeTenantRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static Ok<List<PlanResponse>> ListPlans(IOptions<BillingOptions> billing) =>
        TypedResults.Ok(billing.Value.Plans.Select(p => new PlanResponse(p.Key, p.Value.Name, p.Value.ConversationsPerMonth)).ToList());

    private static async Task<Ok<List<AdminTenantItem>>> ListAsync(
        SuperAdminRepository superAdmin,
        IOptions<BillingOptions> billing,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var tenants = await superAdmin.ListTenantsAsync(cancellationToken);
        var stats = await StatsAsync(superAdmin, null, time, cancellationToken);
        return TypedResults.Ok(tenants.Select(t => ToItem(t, stats, billing.Value)).ToList());
    }

    private static async Task<Ok<AdminTenantDetail>> GetAsync(
        Guid tenantId,
        SuperAdminRepository superAdmin,
        IOptions<BillingOptions> billing,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var tenant = await superAdmin.GetTenantAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        var stats = await StatsAsync(superAdmin, tenantId, time, cancellationToken);
        var monthStart = DashboardStats.Window(time.GetUtcNow(), 1).MonthStart;
        var firstMonth = SuperAdminRepository.MonthOf(monthStart).AddMonths(-11);
        var costs = (await superAdmin.MonthlyCostsAsync(new DateTimeOffset(firstMonth.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime(), tenantId, cancellationToken))
            .ToDictionary(c => c.Cost.Month, c => c.Cost);
        var months = Enumerable.Range(0, 12)
            .Select(i => firstMonth.AddMonths(i))
            .Select(m => costs.TryGetValue(m, out var c) ? new AdminMonthlyCost(m, c.CostUsd, c.Calls) : new AdminMonthlyCost(m, 0m, 0))
            .ToList();
        return TypedResults.Ok(new AdminTenantDetail(
            ToItem(tenant, stats, billing.Value),
            stats.Members.Where(m => m.TenantId == tenantId)
                .Select(m => new AdminMember(m.Member.UserId, m.Member.Email, m.Member.Name, m.Member.Role.ToString().ToLowerInvariant()))
                .ToList(),
            months));
    }

    private static async Task<Ok<CreateTenantResponse>> CreateAsync(CreateTenantRequest request, TenantAdminService admin, CancellationToken cancellationToken)
    {
        var created = await admin.CreateAsync(
            new NewTenant(request.Name, request.IndustrySlug, request.BotName, request.OwnerEmail, request.OwnerName, request.Plan, request.ExpiresAt, request.ConversationQuota),
            cancellationToken);
        return TypedResults.Ok(new CreateTenantResponse(created.TenantId, created.OwnerEmail, created.TemporaryPassword));
    }

    private static async Task<NoContent> UpdateSubscriptionAsync(
        Guid tenantId, UpdateSubscriptionRequest request, TenantAdminService admin, CancellationToken cancellationToken)
    {
        await admin.UpdateSubscriptionAsync(tenantId, request.Plan, request.ExpiresAt, request.ConversationQuota, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SuspendAsync(Guid tenantId, TenantAdminService admin, CancellationToken cancellationToken)
    {
        await admin.SuspendAsync(tenantId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResumeAsync(Guid tenantId, TenantAdminService admin, CancellationToken cancellationToken)
    {
        await admin.ResumeAsync(tenantId, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>File zip toàn bộ dữ liệu DN, ghi thẳng ra response (không giữ cả file trong bộ nhớ).</summary>
    private static async Task<PushStreamHttpResult> ExportAsync(
        Guid tenantId, SuperAdminRepository superAdmin, TenantAdminService admin, CancellationToken cancellationToken)
    {
        _ = await superAdmin.GetTenantAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        var stamp = DateTime.UtcNow.AddHours(7).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return TypedResults.Stream(stream => admin.ExportAsync(tenantId, stream, cancellationToken), "application/zip", $"du-lieu-doanh-nghiep-{stamp}.zip");
    }

    private static async Task<NoContent> PurgeAsync(Guid tenantId, PurgeTenantRequest request, TenantAdminService admin, CancellationToken cancellationToken)
    {
        await admin.PurgeAsync(tenantId, request.ConfirmName, cancellationToken);
        return TypedResults.NoContent();
    }

    private sealed record Stats(
        Dictionary<Guid, int> Conversations,
        Dictionary<(Guid TenantId, DateOnly Month), decimal> Costs,
        IReadOnlyList<(Guid TenantId, TenantMemberRow Member)> Members,
        DateOnly ThisMonth);

    private static async Task<Stats> StatsAsync(SuperAdminRepository superAdmin, Guid? tenantId, TimeProvider time, CancellationToken cancellationToken)
    {
        var monthStart = DashboardStats.Window(time.GetUtcNow(), 1).MonthStart;
        var thisMonth = SuperAdminRepository.MonthOf(monthStart);
        var lastMonthStart = new DateTimeOffset(thisMonth.AddMonths(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime();
        var conversations = await superAdmin.CountConversationsAsync(monthStart, tenantId, cancellationToken);
        var costs = (await superAdmin.MonthlyCostsAsync(lastMonthStart, tenantId, cancellationToken))
            .ToDictionary(c => (c.TenantId, c.Cost.Month), c => c.Cost.CostUsd);
        var members = await superAdmin.ListMembersAsync(tenantId, cancellationToken);
        return new Stats(conversations, costs, members, thisMonth);
    }

    private static AdminTenantItem ToItem(Tenant t, Stats stats, BillingOptions billing)
    {
        var members = stats.Members.Where(m => m.TenantId == t.Id).Select(m => m.Member).ToList();
        return new AdminTenantItem(
            t.Id,
            t.Name,
            t.IndustrySlug,
            IndustryCatalog.All.FirstOrDefault(i => i.Slug == t.IndustrySlug)?.Name ?? t.IndustrySlug,
            t.Plan,
            billing.Plans.TryGetValue(t.Plan, out var plan) ? plan.Name : t.Plan,
            t.Status.ToString().ToLowerInvariant(),
            t.SuspendedReason,
            t.ExpiresAt,
            t.CreatedAt,
            members.FirstOrDefault(m => m.Role == Core.Tenancy.TenantRole.Owner)?.Email,
            members.Count,
            stats.Conversations.GetValueOrDefault(t.Id),
            billing.QuotaFor(t.Plan, t.ConversationQuota),
            t.ConversationQuota,
            stats.Costs.GetValueOrDefault((t.Id, stats.ThisMonth)),
            stats.Costs.GetValueOrDefault((t.Id, stats.ThisMonth.AddMonths(-1))));
    }
}
