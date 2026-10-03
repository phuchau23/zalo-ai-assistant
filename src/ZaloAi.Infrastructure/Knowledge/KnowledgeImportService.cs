using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Errors;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Infrastructure.Knowledge;

public enum DuplicateResolution
{
    /// <summary>Gộp nội dung mới vào mục cũ (giữ mã cũ).</summary>
    Merge,

    /// <summary>Thêm mục mới, giữ cả mục cũ.</summary>
    KeepBoth,
}

/// <summary>Một mục người dùng chọn áp dụng. <see cref="Resolution"/> bắt buộc với mục "có thể trùng".</summary>
public sealed record KnowledgeSelection(string Key, DuplicateResolution? Resolution);

public sealed record KnowledgeApplyResult(int Added, int Updated, int Merged, int Deleted);

/// <summary>Kết quả tạo bản xem trước: có lỗi định dạng thì không tạo (Import = null).</summary>
public sealed record KnowledgePreviewResult(KnowledgeImport? Import, KnowledgeDiff? Diff, IReadOnlyList<KnowledgeParseError> Errors);

/// <summary>
/// Nhập file mẫu theo kiểu review pull request: tạo bản xem trước (không đổi dữ liệu) → người dùng chọn → áp dụng.
/// Mọi thay đổi khi áp dụng nằm trong một transaction; mục bị sửa trong lúc chờ duyệt → từ chối, phải so sánh lại.
/// </summary>
public sealed class KnowledgeImportService(
    AppDbContext db,
    KnowledgeItemRepository items,
    KnowledgeImportRepository imports,
    ChunkRepository chunks,
    AuditLogRepository audit,
    IBackgroundJobClient jobs)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly JsonSerializerOptions DiffJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static KnowledgeDiff ReadDiff(KnowledgeImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        return JsonSerializer.Deserialize<KnowledgeDiff>(import.DiffJson, DiffJsonOptions)
            ?? throw new InvalidOperationException("Bản so sánh bị hỏng.");
    }

    public async Task<KnowledgePreviewResult> CreatePreviewAsync(
        Guid tenantId,
        Guid? userId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var (parsed, format) = extension switch
        {
            ".xlsx" => (KnowledgeExcel.Read(content), KnowledgeImportFormat.Xlsx),
            ".json" => (KnowledgeJsonReader.Read(content), KnowledgeImportFormat.Json),
            _ => throw new InvalidInputException("Chỉ nhận file .xlsx hoặc .json theo mẫu. Tài liệu tự do (PDF, Word...) nạp ở mục Tài liệu tham khảo."),
        };

        if (!parsed.IsValid)
        {
            return new KnowledgePreviewResult(null, null, parsed.Errors);
        }

        var existing = await items.ListAsync(tenantId, kind: null, cancellationToken);
        var diff = KnowledgeDiffer.Compare(parsed.Entries, existing);

        var import = imports.Add(tenantId, new KnowledgeImport
        {
            FileName = Path.GetFileName(fileName),
            SourceFormat = format,
            DiffJson = JsonSerializer.Serialize(diff, DiffJsonOptions),
            CreatedBy = userId,
        });
        audit.Add(tenantId, userId, "knowledge.import_previewed", import.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);

        return new KnowledgePreviewResult(import, diff, []);
    }

    public async Task<KnowledgeApplyResult> ApplyAsync(
        Guid tenantId,
        Guid? userId,
        Guid importId,
        IReadOnlyList<KnowledgeSelection> selections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var import = await imports.GetAsync(tenantId, importId, cancellationToken) ?? throw new NotFoundException();
        EnsurePreview(import);

        var diff = ReadDiff(import);
        var byKey = diff.Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var chosen = Validate(selections, byKey);

        var codes = chosen
            .SelectMany(c => new[] { c.Entry.Code, c.Entry.ExistingCode })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var current = (await items.GetByCodesAsync(tenantId, codes, cancellationToken))
            .ToDictionary(i => i.Code, StringComparer.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var toIndex = new List<KnowledgeItem>();
        int added = 0, updated = 0, merged = 0, deleted = 0;

        foreach (var (entry, resolution) in chosen)
        {
            switch (entry.Type)
            {
                case KnowledgeChangeType.Added:
                case KnowledgeChangeType.PossibleDuplicate when resolution == DuplicateResolution.KeepBoth:
                    if (current.ContainsKey(entry.Code))
                    {
                        throw Stale(entry.Code);
                    }

                    toIndex.Add(items.Add(tenantId, KnowledgeItemMapper.ToItem(Incoming(entry, entry.Code), userId)));
                    added++;
                    break;

                case KnowledgeChangeType.Changed:
                    var changing = CurrentUnchanged(current, entry);
                    KnowledgeItemMapper.Apply(changing, Incoming(entry, changing.Code), userId);
                    await chunks.DeleteForItemAsync(tenantId, changing.Id, cancellationToken);
                    toIndex.Add(changing);
                    updated++;
                    break;

                case KnowledgeChangeType.PossibleDuplicate:
                    // Gộp: nội dung mới, giữ mã của mục cũ.
                    var target = CurrentUnchanged(current, entry);
                    KnowledgeItemMapper.Apply(target, Incoming(entry, target.Code), userId);
                    await chunks.DeleteForItemAsync(tenantId, target.Id, cancellationToken);
                    toIndex.Add(target);
                    merged++;
                    break;

                case KnowledgeChangeType.Missing:
                    items.Remove(tenantId, CurrentUnchanged(current, entry));
                    deleted++;
                    break;
            }
        }

        // Đoạn mới chưa có embedding; bước đánh chỉ mục (job) sẽ tạo vector.
        chunks.AddRange(tenantId, toIndex.Select(BuildChunk));

        import.Status = KnowledgeImportStatus.Applied;
        import.AppliedBy = userId;
        import.AppliedAt = DateTimeOffset.UtcNow;
        import.SelectionJson = JsonSerializer.Serialize(selections, DiffJsonOptions);
        audit.Add(tenantId, userId, "knowledge.import_applied",
            $"{import.Id} +{added} ~{updated} gop{merged} -{deleted}");

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (toIndex.Count > 0)
        {
            jobs.Enqueue<IndexKnowledgeJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        }

        return new KnowledgeApplyResult(added, updated, merged, deleted);
    }

    public async Task DiscardAsync(Guid tenantId, Guid? userId, Guid importId, CancellationToken cancellationToken)
    {
        var import = await imports.GetAsync(tenantId, importId, cancellationToken) ?? throw new NotFoundException();
        EnsurePreview(import);
        import.Status = KnowledgeImportStatus.Discarded;
        audit.Add(tenantId, userId, "knowledge.import_discarded", import.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Một mục kiến thức = một đoạn (mục ngắn, không cần chia).</summary>
    public static Chunk BuildChunk(KnowledgeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var meta = JsonSerializer.Serialize(new
        {
            source = "item",
            kind = item.Kind.ToString().ToLowerInvariant(),
            code = item.Code,
            title = KnowledgeItemMapper.FromItem(item).Title,
        }, DiffJsonOptions);

        return new Chunk { KnowledgeItemId = item.Id, Ordinal = 0, Content = item.SearchText, MetaJson = meta };
    }

    private static void EnsurePreview(KnowledgeImport import)
    {
        if (import.Status != KnowledgeImportStatus.Preview)
        {
            throw new ConflictException(import.Status == KnowledgeImportStatus.Applied
                ? "Lần nhập này đã được áp dụng."
                : "Lần nhập này đã bị hủy. Hãy nhập lại file.");
        }
    }

    private static List<(KnowledgeDiffEntry Entry, DuplicateResolution? Resolution)> Validate(
        IReadOnlyList<KnowledgeSelection> selections,
        Dictionary<string, KnowledgeDiffEntry> byKey)
    {
        if (selections.Count == 0)
        {
            throw new InvalidInputException("Chưa chọn mục nào để áp dụng.");
        }

        var chosen = new List<(KnowledgeDiffEntry, DuplicateResolution?)>();
        foreach (var selection in selections.DistinctBy(s => s.Key))
        {
            if (!byKey.TryGetValue(selection.Key, out var entry))
            {
                throw new InvalidInputException($"Mục \"{selection.Key}\" không có trong bản so sánh.");
            }

            if (entry.Type == KnowledgeChangeType.PossibleDuplicate && selection.Resolution is null)
            {
                throw new InvalidInputException($"Mục \"{entry.Title}\" có thể trùng với \"{entry.ExistingTitle}\": hãy chọn gộp vào mục cũ hoặc giữ cả hai.");
            }

            chosen.Add((entry, entry.Type == KnowledgeChangeType.PossibleDuplicate ? selection.Resolution : null));
        }

        var mergeTargets = chosen
            .Where(c => c.Item1.Type == KnowledgeChangeType.PossibleDuplicate && c.Item2 == DuplicateResolution.Merge)
            .Select(c => c.Item1.ExistingCode)
            .ToHashSet(StringComparer.Ordinal);
        if (chosen.FirstOrDefault(c => c.Item1.Type == KnowledgeChangeType.Missing && mergeTargets.Contains(c.Item1.Code)) is { Item1: { } conflict })
        {
            throw new InvalidInputException($"Không thể vừa gộp vào vừa xóa mục \"{conflict.Title}\" ({conflict.Code}). Bỏ chọn một trong hai.");
        }

        return chosen;
    }

    private static KnowledgeEntry Incoming(KnowledgeDiffEntry entry, string code) =>
        KnowledgeItemMapper.FromDataJson(entry.Kind, code,
            entry.IncomingDataJson ?? throw new InvalidOperationException("Thiếu nội dung mới trong bản so sánh."));

    /// <summary>Mục hiện tại phải còn đúng như lúc so sánh; bị sửa/xóa trong lúc chờ duyệt → 409.</summary>
    private static KnowledgeItem CurrentUnchanged(Dictionary<string, KnowledgeItem> current, KnowledgeDiffEntry entry)
    {
        var code = entry.ExistingCode ?? entry.Code;
        return current.TryGetValue(code, out var item) && item.ContentHash == entry.ExistingHash
            ? item
            : throw Stale(code);
    }

    private static ConflictException Stale(string code) =>
        new($"Mục {code} đã bị thay đổi sau khi tạo bản so sánh. Hãy nhập lại file để so sánh với dữ liệu mới nhất.");
}
