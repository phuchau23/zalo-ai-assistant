using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Knowledge;

public sealed record KnowledgeSearchRequest(string Query, int? Limit);

/// <param name="Score">Độ giống 0..1 (1 = giống hệt).</param>
/// <param name="Source">item | document</param>
public sealed record KnowledgeSearchResult(
    string Source,
    Guid? KnowledgeItemId,
    Guid? DocumentId,
    string? Code,
    string? Title,
    string Content,
    double Score);

public sealed record KnowledgeStatusResponse(int Items, int Documents, int Chunks, int ChunksPendingIndex, int DocumentsProcessing);

/// <param name="Status">pending | processing | ready | failed</param>
public sealed record KnowledgeDocumentResponse(
    Guid Id,
    string Title,
    string FileName,
    long SizeBytes,
    string Status,
    string? Error,
    int ChunkCount,
    bool MedicallyReviewed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static KnowledgeDocumentResponse From(KnowledgeDocument d) =>
        new(d.Id, d.Title, d.FileName, d.SizeBytes, d.Status.ToString().ToLowerInvariant(), d.Error, d.ChunkCount, d.MedicallyReviewed, d.CreatedAt, d.UpdatedAt);
}

public sealed record KnowledgeAiPromptResponse(string Prompt);

internal sealed class KnowledgeSearchRequestValidator : AbstractValidator<KnowledgeSearchRequest>
{
    public KnowledgeSearchRequestValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(500).WithName("Câu tìm kiếm");
        RuleFor(x => x.Limit).InclusiveBetween(1, 20).When(x => x.Limit is not null);
    }
}

/// <summary>Thử tìm kiếm, trạng thái đánh chỉ mục, tài liệu tự do. tenantId luôn từ ITenantContext.</summary>
internal static class KnowledgeSearchEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/knowledge").WithTags("Knowledge");

        group.MapPost("/search", SearchAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Validate<KnowledgeSearchRequest>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/status", StatusAsync).RequireTenantRole(TenantRole.Staff);

        group.MapGet("/ai-prompt", () => TypedResults.Ok(new KnowledgeAiPromptResponse(KnowledgeTemplate.AiPrompt)))
            .RequireTenantRole(TenantRole.Staff);

        group.MapGet("/documents", ListDocumentsAsync).RequireTenantRole(TenantRole.Staff);

        // Upload multipart: CSRF chặn bằng cookie SameSite=Lax (xem KnowledgeImportEndpoints).
        group.MapPost("/documents", UploadDocumentAsync)
            .RequireTenantRole(TenantRole.Owner)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(KnowledgeDocumentService.MaxFileBytes + (64 * 1024)))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/documents/{documentId:guid}/medically-reviewed", SetDocumentReviewedAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/items/{itemId:guid}/medically-reviewed", SetItemReviewedAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/documents/{documentId:guid}", DeleteDocumentAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<List<KnowledgeSearchResult>>> SearchAsync(
        KnowledgeSearchRequest request,
        ITenantContext tenant,
        IKnowledgeSearch search,
        CancellationToken cancellationToken)
    {
        var hits = await search.SearchAsync(tenant.RequireTenantId(), request.Query, request.Limit ?? 5, cancellationToken);
        return TypedResults.Ok(hits.Select(h => new KnowledgeSearchResult(
            h.Source,
            h.Source == "item" ? h.SourceId : null,
            h.Source == "document" ? h.SourceId : null,
            h.Code,
            h.Title,
            h.Content,
            h.Score)).ToList());
    }

    private static async Task<Ok<KnowledgeStatusResponse>> StatusAsync(ITenantContext tenant, AppDbContext db, CancellationToken cancellationToken)
    {
        // Chỉ đếm; global query filter + điều kiện tenant_id tường minh.
        var tenantId = tenant.RequireTenantId();
        var items = await db.KnowledgeItems.CountAsync(i => i.TenantId == tenantId, cancellationToken);
        var documents = await db.Documents.CountAsync(d => d.TenantId == tenantId, cancellationToken);
        var processing = await db.Documents.CountAsync(
            d => d.TenantId == tenantId && (d.Status == DocumentStatus.Pending || d.Status == DocumentStatus.Processing), cancellationToken);
        var chunks = await db.Chunks.CountAsync(c => c.TenantId == tenantId, cancellationToken);
        var pending = await db.Chunks.CountAsync(c => c.TenantId == tenantId && c.Embedding == null, cancellationToken);
        return TypedResults.Ok(new KnowledgeStatusResponse(items, documents, chunks, pending, processing));
    }

    private static async Task<Ok<List<KnowledgeDocumentResponse>>> ListDocumentsAsync(
        ITenantContext tenant,
        KnowledgeDocumentRepository documents,
        CancellationToken cancellationToken)
    {
        var list = await documents.ListAsync(tenant.RequireTenantId(), cancellationToken);
        return TypedResults.Ok(list.Select(KnowledgeDocumentResponse.From).ToList());
    }

    private static async Task<Ok<KnowledgeDocumentResponse>> UploadDocumentAsync(
        IFormFile file,
        bool? replace,
        ITenantContext tenant,
        KnowledgeDocumentService service,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var document = await service.UploadAsync(
            tenant.RequireTenantId(), tenant.UserId, file.FileName, file.ContentType, stream, replace ?? false, cancellationToken);
        return TypedResults.Ok(KnowledgeDocumentResponse.From(document));
    }

    private static async Task<NoContent> DeleteDocumentAsync(
        Guid documentId,
        ITenantContext tenant,
        KnowledgeDocumentService service,
        CancellationToken cancellationToken)
    {
        await service.DeleteAsync(tenant.RequireTenantId(), tenant.UserId, documentId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<KnowledgeDocumentResponse>> SetDocumentReviewedAsync(
        Guid documentId,
        SetMedicallyReviewedRequest request,
        ITenantContext tenant,
        KnowledgeDocumentRepository documents,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var document = await documents.GetAsync(tenantId, documentId, cancellationToken) ?? throw new NotFoundException();
        if (document.MedicallyReviewed != request.Reviewed)
        {
            document.MedicallyReviewed = request.Reviewed;
            audit.Add(tenantId, tenant.UserId, request.Reviewed ? "knowledge.medical_review_set" : "knowledge.medical_review_cleared", $"document:{documentId}");
            await documents.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(KnowledgeDocumentResponse.From(document));
    }

    private static async Task<Ok<KnowledgeItemResponse>> SetItemReviewedAsync(
        Guid itemId,
        SetMedicallyReviewedRequest request,
        ITenantContext tenant,
        KnowledgeItemRepository items,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var item = await items.GetAsync(tenantId, itemId, cancellationToken) ?? throw new NotFoundException();
        if (item.MedicallyReviewed != request.Reviewed)
        {
            item.MedicallyReviewed = request.Reviewed;
            audit.Add(tenantId, tenant.UserId, request.Reviewed ? "knowledge.medical_review_set" : "knowledge.medical_review_cleared", $"item:{itemId}");
            await items.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(KnowledgeItemResponse.From(item));
    }
}
