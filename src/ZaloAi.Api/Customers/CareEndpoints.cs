using FluentValidation;
using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Security;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Customers;

/// <param name="Temperature">hot | warm | cold</param>
/// <param name="Trigger">price_no_close | thinking | asked_schedule | complaint_followup | win_back | unused_package | follow_up | other</param>
/// <param name="MessagingDeadline">Hạn còn nhắn được qua kênh (Zalo: 7 ngày sau tin cuối của khách). null = không giới hạn.</param>
/// <param name="FreeUntil">Zalo: tin tư vấn miễn phí tới lúc này (48 giờ sau tin cuối của khách); sau đó có thể tốn phí/quota.</param>
/// <param name="Status">open | done | skipped | expired | autosent (bot đã tự nhắn)</param>
/// <param name="ScheduledSendAt">Bot sẽ tự nhắn lúc này (null = chờ nhân viên).</param>
/// <param name="EscalationReason">Vì sao cần nhân viên: complaint | sensitive | needs_staff_info | draft_blocked | promotional_no_consent | staff_handling | outside_window | other.</param>
/// <param name="LeadStatus">new | interested | hot | won | lost</param>
public sealed record CareSuggestionItem(
    Guid Id,
    Guid ContactId,
    string ContactName,
    string Channel,
    bool IsTest,
    string LeadStatus,
    Guid ConversationId,
    string Temperature,
    string Trigger,
    string Reason,
    string? SuggestedAction,
    string? Draft,
    DateTimeOffset? LastCustomerMessageAt,
    DateTimeOffset? MessagingDeadline,
    DateTimeOffset? FreeUntil,
    Guid? AssignedUserId,
    string? AssignedName,
    string Status,
    string? Outcome,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ScheduledSendAt,
    string? EscalationReason);

/// <param name="Status">done | skipped | open (mở lại)</param>
/// <param name="Outcome">booked | bought | declined | no_response | other</param>
public sealed record UpdateCareSuggestionRequest(string Status, string? Outcome);

public sealed record AssignCareSuggestionRequest(Guid? UserId);

public sealed record CareCountResponse(int Open);

internal sealed class UpdateCareSuggestionRequestValidator : AbstractValidator<UpdateCareSuggestionRequest>
{
    public UpdateCareSuggestionRequestValidator()
    {
        RuleFor(x => x.Status).Must(s => s is "done" or "skipped" or "open").WithMessage("Trạng thái không hợp lệ.");
        RuleFor(x => x.Outcome).Must(o => o is null or "booked" or "bought" or "declined" or "no_response" or "other")
            .WithMessage("Kết quả không hợp lệ.");
    }
}

/// <summary>
/// Trang "Cần chăm sóc" (docs/FEATURE-SPECS.md mục 2). Hệ thống chỉ GỢI Ý; nhân viên mở hội thoại, sửa tin nháp rồi tự gửi.
/// </summary>
internal static class CareEndpoints
{
    private static readonly TimeSpan ZaloFreeWindow = TimeSpan.FromHours(48);

    public static IEndpointRouteBuilder MapCareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/care").WithTags("Care");
        group.MapGet("/suggestions", ListAsync).RequireTenantRole(TenantRole.Staff);
        group.MapGet("/count", CountAsync).RequireTenantRole(TenantRole.Staff);
        group.MapPatch("/suggestions/{suggestionId:guid}", UpdateAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<UpdateCareSuggestionRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/suggestions/{suggestionId:guid}/assign", AssignAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/conversations/{conversationId:guid}/analyze", AnalyzeAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    /// <param name="status">open (mặc định) | done | skipped | expired</param>
    /// <param name="filter">all (mặc định) | mine | hot | expiring</param>
    private static async Task<Ok<List<CareSuggestionItem>>> ListAsync(
        string? status,
        string? filter,
        ITenantContext tenant,
        ContactRepository contacts,
        MembershipRepository memberships,
        IFieldEncryptor encryptor,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var parsed = Enum.TryParse<CareSuggestionStatus>(status ?? "open", ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : CareSuggestionStatus.Open;
        var rows = await contacts.ListSuggestionsAsync(tenantId, parsed, filter ?? "all", tenant.UserId, time.GetUtcNow(), 100, cancellationToken);
        var names = await CustomerEndpoints.MemberNamesAsync(tenantId, memberships, cancellationToken);
        return TypedResults.Ok(rows
            .Select(r => ToItem(r.Suggestion, r.Contact, ContactView.Name(r.Contact, ContactView.LeadFields(r.Contact, encryptor)), names, encryptor))
            .ToList());
    }

    private static async Task<Ok<CareCountResponse>> CountAsync(ITenantContext tenant, ContactRepository contacts, CancellationToken cancellationToken) =>
        TypedResults.Ok(new CareCountResponse(await contacts.CountOpenSuggestionsAsync(tenant.RequireTenantId(), cancellationToken)));

    private static async Task<NoContent> UpdateAsync(
        Guid suggestionId,
        UpdateCareSuggestionRequest request,
        ITenantContext tenant,
        ContactRepository contacts,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var suggestion = await contacts.GetSuggestionAsync(tenantId, suggestionId, cancellationToken) ?? throw new NotFoundException();
        if (request.Status == "open")
        {
            if (suggestion.Status != CareSuggestionStatus.Open
                && await contacts.GetOpenSuggestionAsync(tenantId, suggestion.ContactId, cancellationToken) is not null)
            {
                throw new ConflictException("Khách này đã có một gợi ý đang mở.");
            }

            suggestion.Status = CareSuggestionStatus.Open;
            suggestion.Outcome = null;
            suggestion.ResolvedAt = null;
            suggestion.ResolvedByUserId = null;
        }
        else if (suggestion.Status == CareSuggestionStatus.AutoSent && request.Status == "done")
        {
            suggestion.Outcome = request.Outcome; // bot đã nhắn: nhân viên chỉ ghi kết quả
        }
        else
        {
            suggestion.ScheduledSendAt = null; // nhân viên xử lý/bỏ qua → hủy tin bot đã hẹn gửi
            suggestion.Status = request.Status == "done" ? CareSuggestionStatus.Done : CareSuggestionStatus.Skipped;
            suggestion.Outcome = request.Outcome;
            suggestion.ResolvedAt = time.GetUtcNow();
            suggestion.ResolvedByUserId = tenant.UserId;
        }

        await contacts.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> AssignAsync(
        Guid suggestionId,
        AssignCareSuggestionRequest request,
        ITenantContext tenant,
        ContactRepository contacts,
        MembershipRepository memberships,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (request.UserId is { } assignee && await memberships.GetAsync(tenantId, assignee, cancellationToken) is null)
        {
            throw new InvalidInputException("Người được gán không thuộc doanh nghiệp này.");
        }

        var suggestion = await contacts.GetSuggestionAsync(tenantId, suggestionId, cancellationToken) ?? throw new NotFoundException();
        suggestion.AssignedUserId = request.UserId;
        await contacts.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Nhân viên bấm "AI gợi ý chăm sóc" cho một hội thoại (không chờ khách "nguội"). Mỗi hội thoại tối đa 1 lần / 2 phút (mỗi lần là
    /// một lần gọi AI có tính phí). Chạy nền; kết quả hiện ở trang "Cần chăm sóc" và hồ sơ khách.
    /// </summary>
    private static async Task<Accepted> AnalyzeAsync(
        Guid conversationId,
        ITenantContext tenant,
        ConversationRepository conversations,
        IDistributedStore store,
        IBackgroundJobClient jobs,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken) ?? throw new NotFoundException();
        if (conversation.NeedsAttentionSince is not null)
        {
            throw new ConflictException("Khách đang chờ nhân viên trả lời — hãy trả lời trong Hộp thư trước.", "needs_attention");
        }

        if (!await store.SetIfNotExistsAsync($"care:manual:{conversationId:N}", "1", TimeSpan.FromMinutes(2), cancellationToken))
        {
            throw new ConflictException("Vừa phân tích hội thoại này, thử lại sau 2 phút.", "too_soon");
        }

        jobs.Enqueue<CareAnalysisJob>(job => job.AnalyzeConversationAsync(tenantId, conversationId, CancellationToken.None));
        return TypedResults.Accepted((string?)null);
    }

    internal static CareSuggestionItem ToItem(
        CareSuggestion s,
        Contact contact,
        string contactName,
        IReadOnlyDictionary<Guid, string> names,
        IFieldEncryptor encryptor)
    {
        var zalo = contact.Channel == ChannelKind.Zalo;
        return new CareSuggestionItem(
            s.Id,
            contact.Id,
            contactName,
            CustomerEndpoints.Lower(contact.Channel),
            contact.Channel == ChannelKind.Webchat,
            CustomerEndpoints.Lower(contact.LeadStatus),
            s.ConversationId,
            CustomerEndpoints.Lower(s.Temperature),
            s.Trigger,
            s.ReasonEnc.Length == 0 ? "" : encryptor.Decrypt(s.ReasonEnc),
            s.SuggestedActionEnc is null ? null : encryptor.Decrypt(s.SuggestedActionEnc),
            s.DraftEnc is null ? null : encryptor.Decrypt(s.DraftEnc),
            contact.LastCustomerMessageAt,
            s.MessagingDeadline,
            zalo && contact.LastCustomerMessageAt is { } last ? last + ZaloFreeWindow : null,
            s.AssignedUserId,
            s.AssignedUserId is { } a ? names.GetValueOrDefault(a) : null,
            CustomerEndpoints.Lower(s.Status),
            s.Outcome,
            s.CreatedAt,
            s.UpdatedAt,
            s.ScheduledSendAt,
            s.EscalationReason);
    }
}
