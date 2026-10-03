using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Knowledge;

internal static class KnowledgeEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/knowledge").WithTags("Knowledge");

        group.MapGet("/template", DownloadTemplate)
            .RequireTenantRole(TenantRole.Staff)
            .Produces(StatusCodes.Status200OK, contentType: XlsxContentType);

        group.MapGet("/export", ExportAsync)
            .RequireTenantRole(TenantRole.Staff)
            .Produces(StatusCodes.Status200OK, contentType: XlsxContentType, additionalContentTypes: "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    /// <summary>File Excel mẫu trống (sheet Hướng dẫn + 5 sheet có tiêu đề cột).</summary>
    private static FileContentHttpResult DownloadTemplate()
    {
        using var buffer = new MemoryStream();
        KnowledgeExcel.Write([], buffer);
        return TypedResults.File(buffer.ToArray(), XlsxContentType, "mau-du-lieu-kien-thuc.xlsx");
    }

    /// <summary>Xuất toàn bộ mục hiện có ra đúng mẫu, để sửa rồi nhập lại (sẽ hiện bản so sánh).</summary>
    private static async Task<FileContentHttpResult> ExportAsync(
        string? format,
        ITenantContext tenant,
        KnowledgeItemRepository items,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var kind = (format ?? "xlsx").ToLowerInvariant();
        if (kind is not ("xlsx" or "json"))
        {
            throw new InvalidInputException("Định dạng xuất chỉ hỗ trợ xlsx hoặc json.");
        }

        var entries = (await items.ListAsync(tenantId, kind: null, cancellationToken))
            .Select(KnowledgeItemMapper.FromItem)
            .ToList();

        audit.Add(tenantId, tenant.UserId, "knowledge.exported", $"{kind}:{entries.Count}");
        await audit.SaveChangesAsync(cancellationToken);

        var stamp = DateTime.UtcNow.AddHours(7).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();
        if (kind == "json")
        {
            KnowledgeItemMapper.WriteJsonFile(entries, buffer);
            return TypedResults.File(buffer.ToArray(), "application/json", $"kien-thuc-{stamp}.json");
        }

        KnowledgeExcel.Write(entries, buffer);
        return TypedResults.File(buffer.ToArray(), XlsxContentType, $"kien-thuc-{stamp}.xlsx");
    }
}
