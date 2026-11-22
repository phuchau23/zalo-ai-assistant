using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Billing;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Tenancy;

namespace ZaloAi.Api.Connections;

/// <param name="UsdVnd">Giá bán USD của Vietcombank ngày đó.</param>
/// <param name="RateApproximated">Không lấy được tỷ giá đúng ngày → dùng giá ngày gần nhất.</param>
public sealed record NickCostDayResponse(DateOnly Date, decimal CostUsd, decimal UsdVnd, decimal CostVnd, bool RateApproximated);

/// <param name="ConnectionId">null = phần không thuộc nick nào (Chat thử, kho kiến thức, hỏi số liệu...).</param>
/// <param name="Channel">zalopersonal | zalo | null</param>
/// <param name="ContactsReached">Số người nick đã nhắn/trò chuyện trong kỳ.</param>
/// <param name="ContactsReplied">Số người đã nhắn lại trong kỳ.</param>
/// <param name="Appointments">Lịch hẹn tạo trong kỳ từ hội thoại của nick (không tính lịch đã hủy).</param>
public sealed record NickCostResponse(
    Guid? ConnectionId,
    string? Name,
    string? Channel,
    decimal CostUsd,
    decimal CostVnd,
    int ChatCalls,
    int EmbedCalls,
    long InputTokens,
    long OutputTokens,
    int ContactsReached,
    int ContactsReplied,
    int Appointments,
    IReadOnlyList<NickCostDayResponse> Days);

/// <param name="RateSource">vietcombank (giá bán USD theo từng ngày)</param>
/// <param name="CardFeePercent">Phí thẻ đã cộng vào VND (%).</param>
/// <param name="AverageRate">Giá bán USD trung bình của các ngày có chi phí.</param>
/// <param name="ApproximatedDays">Ngày có chi phí mà không lấy được tỷ giá đúng ngày.</param>
public sealed record NickCostReportResponse(
    DateOnly From,
    DateOnly To,
    string RateSource,
    decimal CardFeePercent,
    decimal? AverageRate,
    IReadOnlyList<DateOnly> ApproximatedDays,
    IReadOnlyList<NickCostResponse> Nicks,
    NickCostResponse Other,
    decimal TotalUsd,
    decimal TotalVnd);

/// <summary>
/// Chi phí AI theo từng nick, quy ra VND theo tỷ giá Vietcombank từng ngày (chủ dự án chốt 2026-11-15: super admin + chủ DN xem).
/// Chủ DN: <c>GET /channels/costs</c> (DN của mình). Super admin: <c>GET /admin/tenants/{id}/costs</c> — chạy trong scope có tenant
/// context = DN đó, nên vẫn qua lọc tenant như code thường.
/// </summary>
internal static class CostEndpoints
{
    public static IEndpointRouteBuilder MapCostEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/channels/costs", OwnerAsync).WithTags("Channels").RequireTenantRole(TenantRole.Owner);
        app.MapGet("/admin/tenants/{tenantId:guid}/costs", AdminAsync)
            .WithTags("Admin")
            .RequireSuperAdmin()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<NickCostReportResponse>> OwnerAsync(
        DateOnly? from, DateOnly? to, ITenantContext tenant, NickCostService costs, CancellationToken cancellationToken) =>
        TypedResults.Ok(ToResponse(await costs.ReportAsync(tenant.RequireTenantId(), from, to, cancellationToken)));

    private static async Task<Ok<NickCostReportResponse>> AdminAsync(
        Guid tenantId,
        DateOnly? from,
        DateOnly? to,
        ITenantContext caller,
        SuperAdminRepository superAdmin,
        IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        _ = await superAdmin.GetTenantAsync(tenantId, cancellationToken) ?? throw new NotFoundException();
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId, caller.UserId, TenantRole.Owner, isSuperAdmin: true);
        var report = await scope.ServiceProvider.GetRequiredService<NickCostService>().ReportAsync(tenantId, from, to, cancellationToken);
        return TypedResults.Ok(ToResponse(report));
    }

    private static NickCostReportResponse ToResponse(NickCostReport r) => new(
        r.From, r.To, r.RateSource, r.CardFeePercent, r.AverageRate, r.ApproximatedDays, r.Nicks.Select(ToResponse).ToList(), ToResponse(r.Other), r.TotalUsd, r.TotalVnd);

    private static NickCostResponse ToResponse(NickCost n) => new(
        n.ConnectionId,
        n.Name,
        n.Channel,
        n.CostUsd,
        n.CostVnd,
        n.ChatCalls,
        n.EmbedCalls,
        n.InputTokens,
        n.OutputTokens,
        n.ContactsReached,
        n.ContactsReplied,
        n.Appointments,
        n.Days.Select(d => new NickCostDayResponse(d.Date, d.CostUsd, d.UsdVnd, d.CostVnd, d.RateApproximated)).ToList());
}
