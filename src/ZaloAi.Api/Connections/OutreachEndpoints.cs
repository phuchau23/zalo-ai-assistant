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
/// <param name="Attested">Bắt buộc true (trừ khi xem trước): "các số này là người đã liên hệ, đã dùng dịch vụ hoặc được khách giới thiệu; không phải danh sách mua".</param>
/// <param name="ConnectionId">Giao hết cho nick này (khi không chia). null = nick đang chạy đầu tiên.</param>
/// <param name="Distribute">true = chia tự động cho các nick đang chạy, nick khỏe nhận nhiều hơn, khách quen giữ nick cũ.</param>
/// <param name="ConnectionIds">Khi chia: chỉ chia cho các nick này (null/rỗng = mọi nick đang chạy).</param>
/// <param name="DryRun">true = chỉ xem trước số hợp lệ và cách chia, không lưu gì.</param>
public sealed record CreateOutreachBatchRequest(
    string Phones,
    string Origin,
    string? Note,
    string? ReferrerName,
    string? ReferrerPhone,
    Guid? ConnectionId,
    bool Attested,
    bool Distribute = false,
    List<Guid>? ConnectionIds = null,
    bool DryRun = false);

/// <param name="Reason">invalid | duplicate | opted_out | already_chatting | already_queued | contacted_recently (đã liên hệ trong 90 ngày) | contacted_twice (đã liên hệ 2 lần, không trả lời)</param>
/// <param name="RetryAfter">contacted_recently: từ lúc này dán lại được.</param>
public sealed record OutreachSkipResponse(string Phone, string Reason, DateTimeOffset? RetryAfter = null);

/// <param name="QueuedAfter">Hàng chờ của nick sau khi nhận thêm.</param>
public sealed record OutreachAllocationResponse(Guid ConnectionId, string? NickName, int Assigned, int QueuedAfter, int EtaDays);

/// <param name="BatchId">null khi chỉ xem trước.</param>
public sealed record OutreachBatchResponse(
    Guid? BatchId, int Accepted, IReadOnlyList<OutreachSkipResponse> Skipped, int EtaDays, IReadOnlyList<OutreachAllocationResponse> Allocations, bool DryRun);

/// <param name="State">queued | nozalo | invited | friend | messaged | replied | declined | expired | noreply (cần gọi điện) | stopped | failed | converted (đã thành khách) | cold (đã nguội) | archived (lưu trữ)</param>
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
/// <param name="MoveToConnectionId">Chuyển sang nick này — chỉ người đang chờ lượt (chưa được nick cũ liên hệ).</param>
public sealed record UpdateOutreachTargetsRequest(List<Guid> Ids, Guid? AssignedUserId, bool Stop, Guid? MoveToConnectionId = null);

/// <param name="State">Trạng thái hiện tại của người đó (invited | messaged | replied | nozalo ...).</param>
public sealed record OutreachActivityResponse(Guid TargetId, string? Name, string? PhoneMasked, string State, DateTimeOffset At);

/// <param name="Level">normal | slow | paused</param>
/// <param name="NextSendAt">Ngoài khung giờ được nhắn → lúc hàng đợi chạy lại; trong khung giờ → null.</param>
/// <param name="StateCounts">Số người theo trạng thái (queued, invited, friend, messaged, replied, nozalo, noreply, declined, expired, stopped, failed).</param>
/// <param name="Booked">Số người nick đã liên hệ được mà đang có lịch hẹn sắp tới.</param>
/// <param name="Recent">Việc gần nhất của nick, mới nhất trước.</param>
/// <param name="HoldingForReplies">Hôm nay nick đã dùng 70% lượt tin → tạm ngưng nhắn người mới, dành lượt trả lời khách đang chat.</param>
public sealed record OutreachSummaryResponse(
    Guid ConnectionId,
    string? NickName,
    PacingLimitsResponse Limits,
    PacingUsageResponse Usage,
    int Queued,
    int EtaDays,
    string Level,
    DateTimeOffset? NextSendAt,
    Dictionary<string, int> StateCounts,
    int Booked,
    IReadOnlyList<OutreachActivityResponse> Recent,
    bool HoldingForReplies);

internal sealed class CreateOutreachBatchRequestValidator : AbstractValidator<CreateOutreachBatchRequest>
{
    public CreateOutreachBatchRequestValidator()
    {
        RuleFor(x => x.Phones).NotEmpty().MaximumLength(60_000).WithName("Danh sách SĐT");
        RuleFor(x => x.Origin).Must(o => OutreachService.Origins.Contains(o)).WithMessage("Nguồn phải là referral, inquiry hoặc customer.");
        RuleFor(x => x.Note).MaximumLength(500).WithName("Ghi chú cho AI");
        RuleFor(x => x.ReferrerName).MaximumLength(100).WithName("Người giới thiệu");
        RuleFor(x => x.ReferrerPhone).MaximumLength(20).WithName("SĐT người giới thiệu");
        RuleFor(x => x.Attested).Equal(true).When(x => !x.DryRun).WithMessage("Cần xác nhận nguồn của các số điện thoại.");
        RuleFor(x => x.ConnectionIds).Must(ids => ids is null || ids.Count <= 50).WithMessage("Chọn tối đa 50 nick.");
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
        group.MapPost("/nicks/{connectionId:guid}/redistribute", RedistributeAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
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
            cancellationToken,
            request.Distribute,
            request.ConnectionIds,
            request.DryRun);
        return TypedResults.Ok(new OutreachBatchResponse(
            result.BatchId,
            result.Accepted,
            result.Skipped.Select(s => new OutreachSkipResponse(s.Phone, s.Reason, s.RetryAfter)).ToList(),
            result.EtaDays,
            result.Allocations.Select(ToResponse).ToList(),
            result.DryRun));
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
            tenant.RequireTenantId(), tenant.UserId, request.Ids, request.AssignedUserId, request.Stop, cancellationToken, request.MoveToConnectionId));

    private static async Task<Ok<List<OutreachAllocationResponse>>> RedistributeAsync(
        Guid connectionId, ITenantContext tenant, OutreachService service, CancellationToken cancellationToken) =>
        TypedResults.Ok((await service.RedistributeAsync(tenant.RequireTenantId(), tenant.UserId, connectionId, cancellationToken)).Select(ToResponse).ToList());

    private static OutreachAllocationResponse ToResponse(OutreachAllocation a) => new(a.ConnectionId, a.NickName, a.Assigned, a.QueuedAfter, a.EtaDays);

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
            ChannelConnectionResponse.Lower(s.Level),
            s.NextSendAt,
            s.StateCounts.ToDictionary(kv => ChannelConnectionResponse.Lower(kv.Key), kv => kv.Value),
            s.Booked,
            s.Recent.Select(r => new OutreachActivityResponse(r.TargetId, r.Name, r.PhoneMasked, ChannelConnectionResponse.Lower(r.State), r.At)).ToList(),
            s.HoldingForReplies)).ToList());
    }
}
