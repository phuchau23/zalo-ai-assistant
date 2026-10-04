using System.Text.Json;
using FluentValidation;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Knowledge;

namespace ZaloAi.Api.Knowledge;

public sealed record KnowledgeFieldChangeResponse(string Field, string Label, string FieldType, string? Old, string? New);

/// <param name="Type">added | changed | possibleDuplicate | missing</param>
/// <param name="Kind">info | service | package | faq | policy</param>
public sealed record KnowledgeDiffItemResponse(
    string Key,
    string Type,
    string Kind,
    string KindLabel,
    string Code,
    string Title,
    string? ExistingCode,
    string? ExistingTitle,
    double? Similarity,
    bool DefaultSelected,
    IReadOnlyList<KnowledgeFieldChangeResponse> Changes);

public sealed record KnowledgeDiffSummaryResponse(int Added, int Changed, int PossibleDuplicate, int Missing, int Unchanged);

/// <param name="Status">preview | applied | discarded</param>
public sealed record KnowledgeImportSummaryResponse(
    Guid Id,
    string FileName,
    string SourceFormat,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt,
    KnowledgeDiffSummaryResponse Summary);

public sealed record KnowledgeImportResponse(
    Guid Id,
    string FileName,
    string SourceFormat,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt,
    KnowledgeDiffSummaryResponse Summary,
    IReadOnlyList<KnowledgeDiffItemResponse> Items,
    IReadOnlyList<string> SelectedKeys)
{
    internal static KnowledgeImportResponse From(KnowledgeImport import)
    {
        var diff = KnowledgeImportService.ReadDiff(import);
        var selected = import.SelectionJson is null
            ? []
            : JsonSerializer.Deserialize<List<SelectionKey>>(import.SelectionJson, JsonSerializerOptions.Web)?.Select(s => s.Key).ToList() ?? [];

        return new KnowledgeImportResponse(
            import.Id,
            import.FileName,
            Lower(import.SourceFormat),
            Lower(import.Status),
            import.CreatedAt,
            import.AppliedAt,
            ToSummary(diff.Summary),
            diff.Entries.Select(e => new KnowledgeDiffItemResponse(
                e.Key,
                JsonNamingPolicy.CamelCase.ConvertName(e.Type.ToString()),
                Lower(e.Kind),
                KnowledgeTemplate.For(e.Kind).Label,
                e.Code,
                e.Title,
                e.ExistingCode,
                e.ExistingTitle,
                e.Similarity,
                e.DefaultSelected,
                e.Changes.Select(c => new KnowledgeFieldChangeResponse(c.Field, c.Label, c.FieldType, c.Old, c.New)).ToList()))
                .ToList(),
            selected);
    }

    private sealed record SelectionKey(string Key);

    internal static KnowledgeDiffSummaryResponse ToSummary(KnowledgeDiffSummary s) =>
        new(s.Added, s.Changed, s.PossibleDuplicate, s.Missing, s.Unchanged);

    internal static string Lower<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}

/// <param name="Resolution">Chỉ cho mục possibleDuplicate: merge (gộp vào mục cũ) | keepBoth (giữ cả hai).</param>
public sealed record KnowledgeSelectionRequest(string Key, string? Resolution);

public sealed record ApplyKnowledgeImportRequest(IReadOnlyList<KnowledgeSelectionRequest> Selections);

public sealed record KnowledgeApplyResponse(int Added, int Updated, int Merged, int Deleted);

public sealed record KnowledgeFieldValueResponse(string Field, string Label, string FieldType, string Value);

public sealed record KnowledgeItemResponse(
    Guid Id,
    string Kind,
    string KindLabel,
    string Code,
    string Title,
    IReadOnlyList<KnowledgeFieldValueResponse> Fields,
    bool MedicallyReviewed,
    DateTimeOffset UpdatedAt)
{
    internal static KnowledgeItemResponse From(KnowledgeItem item)
    {
        var entry = KnowledgeItemMapper.FromItem(item);
        var definition = KnowledgeTemplate.For(item.Kind);
        var fields = KnowledgeDiffer.FieldChanges(null, entry)
            .Select(c => new KnowledgeFieldValueResponse(c.Field, c.Label, c.FieldType, c.New ?? ""))
            .ToList();
        return new KnowledgeItemResponse(item.Id, KnowledgeImportResponse.Lower(item.Kind), definition.Label, item.Code, entry.Title, fields, item.MedicallyReviewed, item.UpdatedAt);
    }
}

public sealed record KnowledgeFileErrorResponse(string Location, int? Row, string? Column, string Message);

internal sealed class ApplyKnowledgeImportRequestValidator : AbstractValidator<ApplyKnowledgeImportRequest>
{
    public ApplyKnowledgeImportRequestValidator()
    {
        RuleFor(x => x.Selections).NotEmpty().WithMessage("Chưa chọn mục nào để áp dụng.");
        RuleFor(x => x.Selections.Count).LessThanOrEqualTo(KnowledgeTemplate.MaxEntries * 2).When(x => x.Selections is not null);
        RuleForEach(x => x.Selections).ChildRules(s =>
        {
            s.RuleFor(x => x.Key).NotEmpty().MaximumLength(100);
            s.RuleFor(x => x.Resolution)
                .Must(r => r is null or "merge" or "keepBoth")
                .WithMessage("Cách xử lý mục trùng chỉ nhận merge hoặc keepBoth.");
        });
    }
}

/// <summary>Đánh dấu nội dung đã được người có chuyên môn duyệt (docs/INDUSTRIES.md mục 3). Sửa nội dung → tự bỏ đánh dấu.</summary>
public sealed record SetMedicallyReviewedRequest(bool Reviewed);
