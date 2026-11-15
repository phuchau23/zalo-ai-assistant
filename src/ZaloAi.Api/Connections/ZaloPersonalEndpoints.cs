using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.ZaloPersonal;

namespace ZaloAi.Api.Connections;

/// <param name="ConnectionId">Quét lại QR cho nick đã có (phiên hết hạn) — dùng lại đúng thiết bị + proxy cũ. Bỏ trống = nick mới.</param>
public sealed record PersonalLoginStartRequest(Guid? ConnectionId);

public sealed record PersonalLoginStartResponse(string LoginId);

/// <param name="State">starting | qr | scanned | success | expired | declined | error</param>
/// <param name="QrPng">Ảnh QR PNG base64 (chỉ khi state = qr).</param>
/// <param name="ConnectionId">Có khi state = success.</param>
public sealed record PersonalLoginStatusResponse(string State, string? QrPng, Guid? ConnectionId, string? Error, string? DisplayName);

public sealed record PersonalSettingsRequest(bool TreatFriendsAsCustomers, int StaffPauseMinutes);

/// <param name="Kind">customer | pending | private</param>
public sealed record ZaloPeerResponse(
    Guid Id,
    string ExternalUserId,
    string? DisplayName,
    bool IsFriend,
    string Kind,
    Guid? ContactId,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    internal static ZaloPeerResponse From(ZaloPeer p) =>
        new(p.Id, p.ExternalUserId, p.DisplayName, p.IsFriend, ChannelConnectionResponse.Lower(p.Kind), p.ContactId, p.FirstSeenAt, p.LastSeenAt);
}

/// <summary>Nhịp gửi của nick (docs/ZALO-PERSONAL-PLAN.md mục 7.2).</summary>
/// <param name="Mode">safe | custom</param>
/// <param name="ConfirmRisk">Bắt buộc true khi chuyển sang Tùy chỉnh hoặc tắt phanh tự động.</param>
public sealed record PersonalPacingRequest(
    string Mode, int? InvitesPerDay, int? NewContactsPerDay, int? MessagesPerDay, bool AutoBrake, bool ConfirmRisk);

/// <param name="Kind">customer | private</param>
public sealed record DecidePeerRequest(string Kind);

internal sealed class PersonalSettingsRequestValidator : AbstractValidator<PersonalSettingsRequest>
{
    public PersonalSettingsRequestValidator() =>
        RuleFor(x => x.StaffPauseMinutes).InclusiveBetween(0, 1440).WithName("Số phút tạm dừng bot");
}

internal sealed class PersonalPacingRequestValidator : AbstractValidator<PersonalPacingRequest>
{
    public PersonalPacingRequestValidator() =>
        RuleFor(x => x.Mode).Must(m => m is "safe" or "custom").WithMessage("Chế độ phải là \"safe\" hoặc \"custom\".");
}

internal sealed class DecidePeerRequestValidator : AbstractValidator<DecidePeerRequest>
{
    public DecidePeerRequestValidator() =>
        RuleFor(x => x.Kind).Must(k => k is "customer" or "private").WithMessage("Chỉ chọn \"customer\" hoặc \"private\".");
}

/// <summary>
/// Nick Zalo cá nhân (docs/ZALO-PERSONAL-PLAN.md). Kết nối, cài đặt, người quen: chỉ chủ DN (nick cá nhân lẫn chuyện riêng).
/// tenantId luôn từ ITenantContext. Phiên đăng nhập không bao giờ trả ra.
/// </summary>
internal static class ZaloPersonalEndpoints
{
    public static IEndpointRouteBuilder MapZaloPersonalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/channels/zalo-personal").WithTags("Channels");

        group.MapPost("/login", StartLoginAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/login/{loginId}", GetLoginAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/login/{loginId}/cancel", CancelLoginAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{connectionId:guid}/reconnect", ReconnectAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{connectionId:guid}/settings", UpdateSettingsAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<PersonalSettingsRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/{connectionId:guid}/pacing", UpdatePacingAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<PersonalPacingRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{connectionId:guid}/peers", ListPeersAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/peers/{peerId:guid}", DecidePeerAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<DecidePeerRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Chỉ Development + sidecar giả lập: bảng "Giả lập Zalo cá nhân" trên trang Kênh (CONTRACT.md mục 7).
        group.MapPost("/dev/simulate", SimulateAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ExcludeFromDescription();

        // Hộp thư: bật lại bot ngay ở một hội thoại đang tạm im vì chủ nick vừa nhắn tay.
        app.MapPost("/inbox/conversations/{conversationId:guid}/resume-bot", ResumeBotAsync)
            .WithTags("Inbox")
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<PersonalLoginStartResponse>> StartLoginAsync(
        PersonalLoginStartRequest? request, ITenantContext tenant, ZaloPersonalLoginService login, CancellationToken cancellationToken)
    {
        var loginId = await login.StartAsync(
            tenant.RequireTenantId(), tenant.UserId ?? throw new ForbiddenException(), request?.ConnectionId, cancellationToken);
        return TypedResults.Ok(new PersonalLoginStartResponse(loginId));
    }

    private static async Task<Ok<PersonalLoginStatusResponse>> GetLoginAsync(
        string loginId, ITenantContext tenant, ZaloPersonalLoginService login, CancellationToken cancellationToken)
    {
        var state = await login.GetAsync(tenant.RequireTenantId(), loginId, cancellationToken) ?? throw new NotFoundException();
        return TypedResults.Ok(new PersonalLoginStatusResponse(state.State, state.QrPng, state.ConnectionId, state.Error, state.DisplayName));
    }

    private static async Task<NoContent> CancelLoginAsync(
        string loginId, ITenantContext tenant, ZaloPersonalLoginService login, CancellationToken cancellationToken)
    {
        await login.CancelAsync(tenant.RequireTenantId(), loginId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ReconnectAsync(
        Guid connectionId, ITenantContext tenant, ZaloPersonalConnectionService service, CancellationToken cancellationToken)
    {
        await service.ReconnectAsync(tenant.RequireTenantId(), tenant.UserId, connectionId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UpdateSettingsAsync(
        Guid connectionId,
        PersonalSettingsRequest request,
        ITenantContext tenant,
        ZaloPersonalConnectionService service,
        CancellationToken cancellationToken)
    {
        await service.UpdateSettingsAsync(
            tenant.RequireTenantId(), tenant.UserId, connectionId, request.TreatFriendsAsCustomers, request.StaffPauseMinutes, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> UpdatePacingAsync(
        Guid connectionId,
        PersonalPacingRequest request,
        ITenantContext tenant,
        ZaloPersonalConnectionService service,
        CancellationToken cancellationToken)
    {
        await service.UpdatePacingAsync(
            tenant.RequireTenantId(),
            tenant.UserId,
            connectionId,
            request.Mode == "custom" ? PacingMode.Custom : PacingMode.Safe,
            request.InvitesPerDay,
            request.NewContactsPerDay,
            request.MessagesPerDay,
            request.AutoBrake,
            request.ConfirmRisk,
            cancellationToken);
        return TypedResults.NoContent();
    }

    /// <param name="kind">customer | pending | private; bỏ trống = tất cả.</param>
    private static async Task<Ok<List<ZaloPeerResponse>>> ListPeersAsync(
        Guid connectionId, string? kind, ITenantContext tenant, ZaloPersonalConnectionService service, CancellationToken cancellationToken)
    {
        PeerKind? filter = kind switch
        {
            null or "" => null,
            "customer" => PeerKind.Customer,
            "pending" => PeerKind.Pending,
            "private" => PeerKind.Private,
            _ => throw new InvalidInputException("kind phải là customer, pending hoặc private."),
        };
        var peers = await service.ListPeersAsync(tenant.RequireTenantId(), connectionId, filter, cancellationToken);
        return TypedResults.Ok(peers.Select(ZaloPeerResponse.From).ToList());
    }

    private static async Task<Ok<ZaloPeerResponse>> DecidePeerAsync(
        Guid peerId, DecidePeerRequest request, ITenantContext tenant, ZaloPersonalConnectionService service, CancellationToken cancellationToken)
    {
        var peer = await service.DecidePeerAsync(
            tenant.RequireTenantId(), tenant.UserId, peerId, request.Kind == "customer" ? PeerKind.Customer : PeerKind.Private, cancellationToken);
        return TypedResults.Ok(ZaloPeerResponse.From(peer));
    }

    private static async Task<NoContent> SimulateAsync(
        JsonElement command,
        IHostEnvironment environment,
        IOptions<ZaloPersonalOptions> options,
        SidecarClient sidecar,
        ChannelConnectionRepository connections,
        ZaloPersonalLoginService login,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || options.Value.Mode != ZaloPersonalMode.Fake || command.ValueKind != JsonValueKind.Object)
        {
            throw new NotFoundException();
        }

        // Kể cả giả lập cũng chỉ đụng tới nick / phiên QR của chính DN đang đăng nhập.
        var tenantId = tenant.RequireTenantId();
        if (command.TryGetProperty("connectionId", out var c)
            && (!Guid.TryParse(c.GetString(), out var connectionId)
                || await connections.GetAsync(tenantId, connectionId, cancellationToken) is not { Channel: ChannelKind.ZaloPersonal }))
        {
            throw new NotFoundException();
        }

        if (command.TryGetProperty("loginId", out var l) && await login.GetAsync(tenantId, l.GetString() ?? "", cancellationToken) is null)
        {
            throw new NotFoundException();
        }

        await sidecar.SimulateAsync(command, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ResumeBotAsync(
        Guid conversationId, ITenantContext tenant, ConversationRepository conversations, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var conversation = await conversations.GetConversationAsync(tenantId, conversationId, cancellationToken) ?? throw new NotFoundException();
        conversation.BotPausedUntil = null;
        await conversations.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
