using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Api.Connections;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Api.Admin;

/// <param name="Status">active | failing | retired</param>
public sealed record AdminProxyResponse(
    Guid Id,
    string Label,
    string Endpoint,
    string? Country,
    string? ExitIp,
    string Status,
    int FailCount,
    DateTimeOffset? LastCheckedAt,
    string? LastError,
    DateTimeOffset? CoolingUntil,
    Guid? ConnectionId,
    string? NickName,
    string? TenantName);

/// <param name="Url">http(s)://tài_khoản:mật_khẩu@máy_chủ:cổng — chỉ gửi lên một lần, không bao giờ trả ra lại.</param>
public sealed record AddProxyRequest(string Label, string Url, string? Country);

public sealed record ProxyTestResponse(bool Ok, string? ExitIp, int? LatencyMs, string? Error);

/// <param name="Status">active | needsreauth | disconnected</param>
public sealed record AdminPersonalNickResponse(
    Guid ConnectionId, Guid TenantId, string TenantName, string? NickName, string Status, string ListenerState, Guid? ProxyId);

public sealed record AssignProxyRequest(Guid ProxyId);

internal sealed class AddProxyRequestValidator : AbstractValidator<AddProxyRequest>
{
    public AddProxyRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100).WithName("Tên gợi nhớ");
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500).WithName("Địa chỉ proxy");
        RuleFor(x => x.Country).MaximumLength(50).WithName("Quốc gia");
    }
}

/// <summary>
/// Kho proxy IP riêng cho nick Zalo cá nhân — CHỈ super admin (chủ dự án). Mật khẩu proxy không bao giờ trả ra API; DN không thấy
/// trang này. docs/ZALO-PERSONAL-PLAN.md mục 8.
/// </summary>
internal static class AdminZaloPersonalEndpoints
{
    public static IEndpointRouteBuilder MapAdminZaloPersonalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").WithTags("Admin").RequireSuperAdmin();

        group.MapGet("/proxies", ListProxiesAsync);
        group.MapPost("/proxies", AddProxyAsync)
            .Validate<AddProxyRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/proxies/{proxyId:guid}/test", TestProxyAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/proxies/{proxyId:guid}/retire", RetireProxyAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/zalo-personal/nicks", ListNicksAsync);
        group.MapPut("/zalo-personal/{connectionId:guid}/proxy", AssignProxyAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<Ok<List<AdminProxyResponse>>> ListProxiesAsync(ProxyPoolService pool, CancellationToken cancellationToken)
    {
        var list = await pool.ListAsync(cancellationToken);
        return TypedResults.Ok(list.Select(p => new AdminProxyResponse(
            p.Id, p.Label, p.Endpoint, p.Country, p.ExitIp, ChannelConnectionResponse.Lower(p.Status), p.FailCount, p.LastCheckedAt, p.LastError,
            p.CoolingUntil, p.ConnectionId, p.NickName, p.TenantName)).ToList());
    }

    private static async Task<Ok<AdminProxyResponse>> AddProxyAsync(AddProxyRequest request, ProxyPoolService pool, CancellationToken cancellationToken)
    {
        var p = await pool.AddAsync(request.Label, request.Url, request.Country, cancellationToken);
        return TypedResults.Ok(new AdminProxyResponse(
            p.Id, p.Label, p.Endpoint, p.Country, p.ExitIp, ChannelConnectionResponse.Lower(p.Status), p.FailCount, p.LastCheckedAt, p.LastError,
            p.CoolingUntil, null, null, null));
    }

    private static async Task<Ok<ProxyTestResponse>> TestProxyAsync(Guid proxyId, ProxyPoolService pool, CancellationToken cancellationToken)
    {
        var result = await pool.TestAsync(proxyId, cancellationToken);
        return TypedResults.Ok(new ProxyTestResponse(result.Ok, result.ExitIp, result.LatencyMs, result.Error));
    }

    private static async Task<NoContent> RetireProxyAsync(Guid proxyId, ProxyPoolService pool, CancellationToken cancellationToken)
    {
        await pool.RetireAsync(proxyId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<AdminPersonalNickResponse>>> ListNicksAsync(ProxyPoolService pool, CancellationToken cancellationToken)
    {
        var nicks = await pool.ListNicksAsync(cancellationToken);
        return TypedResults.Ok(nicks.Select(n => new AdminPersonalNickResponse(
            n.ConnectionId, n.TenantId, n.TenantName, n.NickName, ChannelConnectionResponse.Lower(n.Status),
            ChannelConnectionResponse.Lower(n.ListenerState), n.ProxyId)).ToList());
    }

    private static async Task<NoContent> AssignProxyAsync(
        Guid connectionId, AssignProxyRequest request, ProxyPoolService pool, CancellationToken cancellationToken)
    {
        await pool.AssignAsync(connectionId, request.ProxyId, cancellationToken);
        return TypedResults.NoContent();
    }
}
