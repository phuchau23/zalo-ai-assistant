using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Api.Connections;

/// <param name="Phones">Mỗi dòng một SĐT, có thể kèm tên sau dấu phẩy ("0912345678, chị Hoa").</param>
/// <param name="Origin">referral | inquiry | customer</param>
/// <param name="Attested">Bắt buộc true: "các số này là người đã liên hệ, đã dùng dịch vụ hoặc được khách giới thiệu; không phải danh sách mua".</param>
public sealed record CreateOutreachBatchRequest(
    string Phones, string Origin, string? Note, string? ReferrerName, string? ReferrerPhone, Guid? ConnectionId, bool Attested);

/// <param name="Reason">invalid | duplicate | opted_out | already_chatting | already_queued</param>
public sealed record OutreachSkipResponse(string Phone, string Reason);

public sealed record OutreachBatchResponse(Guid BatchId, int Accepted, IReadOnlyList<OutreachSkipResponse> Skipped, int EtaDays);

/// <param name="State">queued | nozalo | invited | friend | messaged | replied | declined | expired | noreply | stopped | failed</param>
/// <param name="PhoneMasked">SĐT che giữa (0912***678).</param>
/// <param name="LeadStatus">new | interested | hot | won | lost</param>
public sealed record OutreachTargetResponse(
    Guid Id,
    Guid ContactId,
    string? Name,
    string? PhoneMasked,
    string State,
    Guid? ConnectionId,
    Guid? AssignedUserId,
    string LeadStatus,
    string? Origin,
    Guid? ConversationId,
    DateTimeOffset? NextAppointmentAt,
    DateTimeOffset? FriendRequestSentAt,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? RepliedAt,
    DateTimeOffset? NextActionAt,
    string? LastError,
    Guid BatchId,
    DateTimeOffset UpdatedAt);

/// <param name="Origin">referral | inquiry | customer</param>
public sealed record OutreachBatchItemResponse(
    Guid Id, DateTimeOffset CreatedAt, string Origin, string? ReferrerName, int AcceptedCount, int SkippedCount, Guid? ConnectionId);

/// <param name="Stop">true = dừng chăm sóc những người này.</param>
public sealed record UpdateOutreachTargetsRequest(List<Guid> Ids, Guid? AssignedUserId, bool Stop);

/// <param name="Level">normal | slow | paused</param>
public sealed record OutreachSummaryResponse(
    Guid ConnectionId, string? NickName, PacingLimitsResponse Limits, PacingUsageResponse Usage, int Queued, int EtaDays, string Level);

internal sealed class CreateOutreachBatchRequestValidator : AbstractValidator<CreateOutreachBatchRequest>
{
    public CreateOutreachBatchRequestValidator()
    {
        RuleFor(x => x.Phones).NotEmpty().MaximumLength(60_000).WithName("Danh sách SĐT");
        RuleFor(x => x.Origin).Must(o => OutreachService.Origins.Contains(o)).WithMessage("Nguồn phải là referral, inquiry hoặc customer.");
        RuleFor(x => x.Note).MaximumLength(500).WithName("Ghi chú cho AI");
        RuleFor(x => x.ReferrerName).MaximumLength(100).WithName("Người giới thiệu");
        RuleFor(x => x.ReferrerPhone).MaximumLength(20).WithName("SĐT người giới thiệu");
        RuleFor(x => x.Attested).Equal(true).WithMessage("Cần xác nhận nguồn của các số điện thoại.");
    }
}

internal sealed class UpdateOutreachTargetsRequestValidator : AbstractValidator<UpdateOutreachTargetsRequest>
{
    public UpdateOutreachTargetsRequestValidator() =>
        RuleFor(x => x.Ids).NotEmpty().Must(i => i.Count <= 500).WithMessage("Chọn từ 1 đến 500 người.");
}

/// <summary>
/// Chăm sóc qua Zalo cá nhân: ô dán SĐT + màn hình "Đang chăm sóc" (docs/ZALO-PERSONAL-PLAN.md mục 6.9). Nhân viên dán được (khách đưa SĐT
/// người quen cho nhân viên). tenantId luôn từ ITenantContext; SĐT trả ra luôn che giữa.
/// </summary>
internal static class OutreachEndpoints
{
    public static IEndpointRouteBuilder MapOutreachEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/zalo-outreach").WithTags("ZaloOutreach");
        group.MapPost("/batches", CreateBatchAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<CreateOutreachBatchRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/batches", ListBatchesAsync).RequireTenantRole(TenantRole.Staff);
        group.MapGet("/targets", ListAsync).RequireTenantRole(TenantRole.Staff);
        group.MapPut("/targets", UpdateAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<UpdateOutreachTargetsRequest>();
        group.MapGet("/summary", SummaryAsync).RequireTenantRole(TenantRole.Staff);
        return app;
    }

    private static async Task<Ok<OutreachBatchResponse>> CreateBatchAsync(
        CreateOutreachBatchRequest request, ITenantContext tenant, OutreachService service, CancellationToken cancellationToken)
    {
        var result = await service.CreateBatchAsync(
            tenant.RequireTenantId(),
            tenant.UserId ?? throw new ForbiddenException(),
            request.Phones,
            request.Origin,
            request.Note,
            request.ReferrerName,
            request.ReferrerPhone,
            request.ConnectionId,
            request.Attested,
            cancellationToken);
        return TypedResults.Ok(new OutreachBatchResponse(
            result.BatchId, result.Accepted, result.Skipped.Select(s => new OutreachSkipResponse(s.Phone, s.Reason)).ToList(), result.EtaDays));
    }

    private static async Task<Ok<List<OutreachBatchItemResponse>>> ListBatchesAsync(
        ITenantContext tenant, OutreachService service, CancellationToken cancellationToken) =>
        TypedResults.Ok((await service.ListBatchesAsync(tenant.RequireTenantId(), cancellationToken))
            .Select(b => new OutreachBatchItemResponse(b.Id, b.CreatedAt, b.Origin, b.ReferrerName, b.AcceptedCount, b.SkippedCount, b.ConnectionId))
            .ToList());

    /// <param name="state">Lọc theo trạng thái (vd queued, invited, messaged, replied, nozalo, noreply...).</param>
    private static async Task<Ok<List<OutreachTargetResponse>>> ListAsync(
        string? state,
        Guid? connectionId,
        Guid? assignedUserId,
        Guid? batchId,
        ITenantContext tenant,
        OutreachRepository outreach,
        IFieldEncryptor encryptor,
        CancellationToken cancellationToken)
    {
        OutreachState? filter = null;
        if (!string.IsNullOrEmpty(state))
        {
            filter = Enum.TryParse<OutreachState>(state, ignoreCase: true, out var s) ? s : throw new InvalidInputException("Trạng thái không hợp lệ.");
        }

        var rows = await outreach.ListBoardAsync(tenant.RequireTenantId(), filter, connectionId, assignedUserId, batchId, cancellationToken);
        return TypedResults.Ok(rows.Select(r => new OutreachTargetResponse(
            r.Target.Id,
            r.Contact.Id,
            r.Contact.DisplayName,
            r.Contact.PhoneEnc is null ? null : PhoneNumber.Mask(encryptor.Decrypt(r.Contact.PhoneEnc)),
            ChannelConnectionResponse.Lower(r.Target.State),
            r.Target.ConnectionId,
            r.Target.AssignedUserId,
            ChannelConnectionResponse.Lower(r.Contact.LeadStatus),
            r.Contact.Origin,
            r.ConversationId,
            r.NextAppointmentAt,
            r.Target.FriendRequestSentAt,
            r.Target.FirstMessageAt,
            r.Target.RepliedAt,
            r.Target.NextActionAt,
            r.Target.LastError,
            r.Target.BatchId,
            r.Target.UpdatedAt)).ToList());
    }

    private static async Task<Ok<int>> UpdateAsync(
        UpdateOutreachTargetsRequest request, ITenantContext tenant, OutreachService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.UpdateTargetsAsync(
            tenant.RequireTenantId(), tenant.UserId, request.Ids, request.AssignedUserId, request.Stop, cancellationToken));

    private static async Task<Ok<List<OutreachSummaryResponse>>> SummaryAsync(
        ITenantContext tenant, OutreachService service, CancellationToken cancellationToken)
    {
        var list = await service.SummaryAsync(tenant.RequireTenantId(), cancellationToken);
        return TypedResults.Ok(list.Select(s => new OutreachSummaryResponse(
            s.ConnectionId,
            s.NickName,
            new PacingLimitsResponse(s.Limits.InvitesPerDay, s.Limits.NewContactsPerDay, s.Limits.MessagesPerDay, s.Limits.MessagesPerHour, s.Limits.FindUserPerDay, s.Limits.NewNickWeek),
            new PacingUsageResponse(s.Usage.Invites, s.Usage.NewContacts, s.Usage.Messages, s.Usage.FindUser),
            s.Queued,
            s.EtaDays,
            ChannelConnectionResponse.Lower(s.Level))).ToList());
    }
}
