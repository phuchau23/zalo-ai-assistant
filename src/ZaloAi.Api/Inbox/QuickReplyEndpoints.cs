using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Inbox;

public sealed record QuickReplyResponse(Guid Id, string Shortcut, string Title, string Content, int SortOrder, DateTimeOffset UpdatedAt)
{
    internal static QuickReplyResponse From(QuickReply q) => new(q.Id, q.Shortcut, q.Title, q.Content, q.SortOrder, q.UpdatedAt);
}

/// <param name="Shortcut">Chữ thường không dấu, số, '-', '_' (vd "gia", "dia-chi"); gõ "/gia" trong hộp thư để chèn.</param>
public sealed record QuickReplyRequest(string Shortcut, string Title, string Content, int SortOrder);

internal sealed class QuickReplyRequestValidator : AbstractValidator<QuickReplyRequest>
{
    public QuickReplyRequestValidator()
    {
        RuleFor(x => x.Shortcut).NotEmpty().MaximumLength(30).Matches("^[a-z0-9_-]+$")
            .WithMessage("Phím tắt chỉ gồm chữ thường không dấu, số, '-' và '_'.");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(100).WithName("Tên mẫu");
        RuleFor(x => x.Content).NotEmpty().MaximumLength(1900).WithName("Nội dung"); // vừa một tin hộp thư
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000).WithName("Thứ tự");
    }
}

/// <summary>Tin nhắn mẫu (docs/ZALO-PERSONAL-PLAN.md Z4). Nhân viên xem để chèn; chủ DN tạo/sửa/xóa. tenantId luôn từ ITenantContext.</summary>
internal static class QuickReplyEndpoints
{
    public const int MaxPerTenant = 200;

    public static IEndpointRouteBuilder MapQuickReplyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/quick-replies").WithTags("QuickReplies");
        group.MapGet("", ListAsync).RequireTenantRole(TenantRole.Staff);
        group.MapPost("", CreateAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<QuickReplyRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<QuickReplyRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<List<QuickReplyResponse>>> ListAsync(ITenantContext tenant, QuickReplyRepository replies, CancellationToken cancellationToken) =>
        TypedResults.Ok((await replies.ListAsync(tenant.RequireTenantId(), cancellationToken)).Select(QuickReplyResponse.From).ToList());

    private static async Task<Ok<QuickReplyResponse>> CreateAsync(
        QuickReplyRequest request, ITenantContext tenant, QuickReplyRepository replies, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (await replies.CountAsync(tenantId, cancellationToken) >= MaxPerTenant)
        {
            throw new ConflictException($"Tối đa {MaxPerTenant} tin nhắn mẫu.", "quick_reply_limit");
        }

        if (await replies.ShortcutTakenAsync(tenantId, request.Shortcut, null, cancellationToken))
        {
            throw new ConflictException("Phím tắt này đã có.", "quick_reply_duplicate");
        }

        var reply = replies.Add(tenantId, new QuickReply
        {
            Shortcut = request.Shortcut,
            Title = request.Title.Trim(),
            Content = request.Content.Trim(),
            SortOrder = request.SortOrder,
            CreatedByUserId = tenant.UserId,
        });
        await replies.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(QuickReplyResponse.From(reply));
    }

    private static async Task<Ok<QuickReplyResponse>> UpdateAsync(
        Guid id, QuickReplyRequest request, ITenantContext tenant, QuickReplyRepository replies, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var reply = await replies.GetAsync(tenantId, id, cancellationToken) ?? throw new NotFoundException();
        if (await replies.ShortcutTakenAsync(tenantId, request.Shortcut, id, cancellationToken))
        {
            throw new ConflictException("Phím tắt này đã có.", "quick_reply_duplicate");
        }

        reply.Shortcut = request.Shortcut;
        reply.Title = request.Title.Trim();
        reply.Content = request.Content.Trim();
        reply.SortOrder = request.SortOrder;
        await replies.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(QuickReplyResponse.From(reply));
    }

    private static async Task<NoContent> DeleteAsync(Guid id, ITenantContext tenant, QuickReplyRepository replies, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var reply = await replies.GetAsync(tenantId, id, cancellationToken) ?? throw new NotFoundException();
        replies.Remove(tenantId, reply);
        await replies.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
