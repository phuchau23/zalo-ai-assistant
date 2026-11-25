using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Api.Customers;

/// <param name="Reason">cold (từng tư vấn rồi im 14 ngày) | noreply (nhận tin chào, im 2 ngày) | nozalo (SĐT không có Zalo)</param>
/// <param name="Phone">SĐT đầy đủ để bấm gọi (xem danh sách có ghi nhật ký truy cập).</param>
/// <param name="Origin">customer | referral | inquiry</param>
/// <param name="Since">Lúc vào danh sách / lần cuối có người xử lý (30 ngày không ai xử lý → lưu trữ).</param>
/// <param name="LastCustomerText">Tin cuối của khách (chỉ với người đã nguội), cắt ngắn.</param>
/// <param name="CallAttempts">Số lần nhân viên đã ghi kết quả gọi.</param>
public sealed record CallListItem(
    Guid TargetId,
    Guid ContactId,
    string Name,
    string? Phone,
    string Salutation,
    string Reason,
    string Origin,
    string? ReferrerName,
    Guid? ConnectionId,
    string? NickName,
    Guid? ConversationId,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? FriendRequestSentAt,
    DateTimeOffset? LastCustomerMessageAt,
    string? LastCustomerText,
    DateTimeOffset Since,
    int CallAttempts,
    DateTimeOffset? LastCallAt);

/// <param name="Result">interested (về Đang tư vấn) | not_interested (ngừng hẳn) | no_answer (giữ trong danh sách)</param>
/// <param name="Note">Ghi chú thêm (tùy chọn), lưu vào nhật ký chăm sóc của khách.</param>
public sealed record CallResultRequest(string Result, string? Note);

internal sealed class CallResultRequestValidator : AbstractValidator<CallResultRequest>
{
    public CallResultRequestValidator()
    {
        RuleFor(x => x.Result).Must(r => OutreachLifecycle.CallResults.Contains(r))
            .WithMessage("Kết quả cuộc gọi phải là interested, not_interested hoặc no_answer.");
        RuleFor(x => x.Note).MaximumLength(500).WithName("Ghi chú");
    }
}

/// <summary>
/// Danh sách "Cần gọi điện" ở trang Cần chăm sóc (chủ dự án chốt 2026-11-25): người bot đã thôi nhắn qua nick Zalo cá nhân. Chủ và nhân viên
/// xem được (SĐT đầy đủ để gọi, mỗi lần xem ghi nhật ký), bấm kết quả cuộc gọi. tenantId luôn từ ITenantContext.
/// </summary>
internal static class CallListEndpoints
{
    public static IEndpointRouteBuilder MapCallListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/care/call-list").WithTags("Care").RequireTenantRole(TenantRole.Staff);
        group.MapGet("", ListAsync);
        group.MapPost("/{targetId:guid}/result", RecordAsync)
            .Validate<CallResultRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<Ok<List<CallListItem>>> ListAsync(ITenantContext tenant, CallListService calls, CancellationToken cancellationToken)
    {
        var rows = await calls.ListAsync(tenant.RequireTenantId(), tenant.UserId, cancellationToken);
        return TypedResults.Ok(rows.Select(r => new CallListItem(
            r.TargetId,
            r.ContactId,
            r.Name,
            r.Phone,
            r.Salutation,
            r.State.ToString().ToLowerInvariant(),
            r.Origin,
            r.ReferrerName,
            r.ConnectionId,
            r.NickName,
            r.ConversationId,
            r.FirstMessageAt,
            r.FriendRequestSentAt,
            r.LastCustomerMessageAt,
            r.LastCustomerText,
            r.Since,
            r.CallAttempts,
            r.LastCallAt)).ToList());
    }

    private static async Task<NoContent> RecordAsync(
        Guid targetId, CallResultRequest request, ITenantContext tenant, CallListService calls, CancellationToken cancellationToken)
    {
        await calls.RecordResultAsync(tenant.RequireTenantId(), tenant.UserId, targetId, request.Result, request.Note, cancellationToken);
        return TypedResults.NoContent();
    }
}
