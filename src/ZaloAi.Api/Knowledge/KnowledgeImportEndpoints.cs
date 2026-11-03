using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Knowledge;

/// <summary>
/// Nhập file mẫu kiểu review pull request. Staff xem; chỉ owner tạo bản so sánh, áp dụng, hủy.
/// tenantId luôn từ ITenantContext (cookie), không có trong route/body.
/// </summary>
internal static class KnowledgeImportEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/knowledge").WithTags("Knowledge");

        group.MapGet("/items", ListItemsAsync).RequireTenantRole(TenantRole.Staff);

        group.MapGet("/imports", ListImportsAsync).RequireTenantRole(TenantRole.Staff);

        group.MapGet("/imports/{importId:guid}", GetImportAsync)
            .RequireTenantRole(TenantRole.Staff)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Upload multipart: không dùng antiforgery token; CSRF bị chặn bởi cookie SameSite=Lax
        // (trình duyệt không gửi cookie khi trang khác POST form sang). Xem DECISIONS.md (M1 bước 4).
        group.MapPost("/imports", CreatePreviewAsync)
            .RequireTenantRole(TenantRole.Owner)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(KnowledgeImportService.MaxFileBytes + (64 * 1024)))
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/imports/{importId:guid}/apply", ApplyAsync)
            .RequireTenantRole(TenantRole.Owner)
            .Validate<ApplyKnowledgeImportRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/imports/{importId:guid}/discard", DiscardAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Ok<List<KnowledgeItemResponse>>> ListItemsAsync(
        string? kind,
        ITenantContext tenant,
        KnowledgeItemRepository items,
        CancellationToken cancellationToken)
    {
        KnowledgeKind? filter = null;
        if (!string.IsNullOrEmpty(kind))
        {
            filter = Enum.TryParse<KnowledgeKind>(kind, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidInputException("Loại mục không hợp lệ (info, service, package, faq, policy).");
        }

        var list = await items.ListAsync(tenant.RequireTenantId(), filter, cancellationToken);
        return TypedResults.Ok(list.Select(KnowledgeItemResponse.From).ToList());
    }

    private static async Task<Ok<List<KnowledgeImportSummaryResponse>>> ListImportsAsync(
        ITenantContext tenant,
        KnowledgeImportRepository imports,
        CancellationToken cancellationToken)
    {
        var list = await imports.ListAsync(tenant.RequireTenantId(), 50, cancellationToken);
        return TypedResults.Ok(list.Select(i => new KnowledgeImportSummaryResponse(
            i.Id,
            i.FileName,
            KnowledgeImportResponse.Lower(i.SourceFormat),
            KnowledgeImportResponse.Lower(i.Status),
            i.CreatedAt,
            i.AppliedAt,
            KnowledgeImportResponse.ToSummary(KnowledgeImportService.ReadDiff(i).Summary))).ToList());
    }

    private static async Task<Ok<KnowledgeImportResponse>> GetImportAsync(
        Guid importId,
        ITenantContext tenant,
        KnowledgeImportRepository imports,
        CancellationToken cancellationToken)
    {
        // Lần nhập của tenant khác → null → 404 (không lộ là có tồn tại).
        var import = await imports.GetAsync(tenant.RequireTenantId(), importId, cancellationToken) ?? throw new NotFoundException();
        return TypedResults.Ok(KnowledgeImportResponse.From(import));
    }

    private static async Task<Results<Ok<KnowledgeImportResponse>, ProblemHttpResult>> CreatePreviewAsync(
        IFormFile file,
        ITenantContext tenant,
        KnowledgeImportService service,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new InvalidInputException("File rỗng.");
        }

        if (file.Length > KnowledgeImportService.MaxFileBytes)
        {
            throw new InvalidInputException($"File quá lớn (tối đa {KnowledgeImportService.MaxFileBytes / 1024 / 1024} MB). Chia thành nhiều file nhỏ hơn.");
        }

        await using var stream = file.OpenReadStream();
        var result = await service.CreatePreviewAsync(tenant.RequireTenantId(), tenant.UserId, file.FileName, stream, cancellationToken);

        if (result.Import is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: $"File có {result.Errors.Count} lỗi, chưa thể so sánh. Sửa các lỗi rồi nhập lại.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "invalid_file",
                    ["fileErrors"] = result.Errors.Select(e => new KnowledgeFileErrorResponse(e.Location, e.Row, e.Column, e.Message)).ToList(),
                });
        }

        return TypedResults.Ok(KnowledgeImportResponse.From(result.Import));
    }

    private static async Task<Ok<KnowledgeApplyResponse>> ApplyAsync(
        Guid importId,
        ApplyKnowledgeImportRequest request,
        ITenantContext tenant,
        KnowledgeImportService service,
        CancellationToken cancellationToken)
    {
        var selections = request.Selections
            .Select(s => new KnowledgeSelection(s.Key, s.Resolution switch
            {
                "merge" => DuplicateResolution.Merge,
                "keepBoth" => DuplicateResolution.KeepBoth,
                _ => null,
            }))
            .ToList();

        var result = await service.ApplyAsync(tenant.RequireTenantId(), tenant.UserId, importId, selections, cancellationToken);
        return TypedResults.Ok(new KnowledgeApplyResponse(result.Added, result.Updated, result.Merged, result.Deleted));
    }

    private static async Task<NoContent> DiscardAsync(
        Guid importId,
        ITenantContext tenant,
        KnowledgeImportService service,
        CancellationToken cancellationToken)
    {
        await service.DiscardAsync(tenant.RequireTenantId(), tenant.UserId, importId, cancellationToken);
        return TypedResults.NoContent();
    }
}
