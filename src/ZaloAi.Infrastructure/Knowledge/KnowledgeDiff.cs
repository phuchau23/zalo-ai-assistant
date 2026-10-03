using System.Globalization;
using System.Text;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Knowledge;

public enum KnowledgeChangeType
{
    /// <summary>Mã chưa có → thêm mới.</summary>
    Added,

    /// <summary>Mã đã có, nội dung khác → cập nhật.</summary>
    Changed,

    /// <summary>Mã mới nhưng tên gần giống một mục đang có → người dùng chọn gộp vào mục cũ hay giữ cả hai.</summary>
    PossibleDuplicate,

    /// <summary>Mục đang có nhưng file không có → mặc định giữ lại, chọn thì xóa.</summary>
    Missing,
}

/// <summary>Một trường khác nhau. Giá trị dạng chuỗi (số → chữ số), null = không có.</summary>
public sealed record KnowledgeFieldChange(string Field, string Label, string FieldType, string? Old, string? New);

/// <summary>
/// Một dòng trong bản so sánh. <see cref="Key"/> là khóa ổn định trong một lần nhập, dùng khi người dùng chọn mục.
/// <see cref="IncomingDataJson"/> là nội dung mới (để áp dụng không cần tải lại file); <see cref="ExistingHash"/> là
/// dấu vân tay mục cũ lúc so sánh (để phát hiện mục bị sửa trong lúc chờ duyệt).
/// </summary>
public sealed record KnowledgeDiffEntry(
    string Key,
    KnowledgeChangeType Type,
    KnowledgeKind Kind,
    string Code,
    string Title,
    string? ExistingCode,
    string? ExistingTitle,
    string? ExistingHash,
    double? Similarity,
    bool DefaultSelected,
    IReadOnlyList<KnowledgeFieldChange> Changes,
    string? IncomingDataJson);

public sealed record KnowledgeDiffSummary(int Added, int Changed, int PossibleDuplicate, int Missing, int Unchanged);

public sealed record KnowledgeDiff(KnowledgeDiffSummary Summary, IReadOnlyList<KnowledgeDiffEntry> Entries);

/// <summary>So sánh các mục đọc từ file với các mục đang có của tenant (không đụng DB).</summary>
public static class KnowledgeDiffer
{
    /// <summary>Tên giống ≥ ngưỡng này (sau khi bỏ dấu, bỏ hoa thường, số phải khớp) thì coi là có thể trùng.</summary>
    public const double DuplicateThreshold = 0.85;

    public static KnowledgeDiff Compare(IReadOnlyList<KnowledgeEntry> incoming, IReadOnlyList<KnowledgeItem> existingItems)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existingItems);

        var existing = existingItems.ToDictionary(i => i.Code, i => (Item: i, Entry: KnowledgeItemMapper.FromItem(i)), StringComparer.Ordinal);
        var incomingCodes = incoming.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var entries = new List<KnowledgeDiffEntry>();
        var unchanged = 0;

        foreach (var entry in incoming)
        {
            var dataJson = KnowledgeItemMapper.ToDataJson(entry);

            if (existing.TryGetValue(entry.Code, out var current))
            {
                if (current.Item.ContentHash == KnowledgeItemMapper.ContentHash(entry))
                {
                    unchanged++;
                    continue;
                }

                entries.Add(new(entry.Code, KnowledgeChangeType.Changed, entry.Kind, entry.Code, entry.Title,
                    current.Item.Code, current.Entry.Title, current.Item.ContentHash, null, DefaultSelected: true,
                    FieldChanges(current.Entry, entry), dataJson));
                continue;
            }

            var match = BestMatch(entry, existing.Values.Select(v => v.Entry));
            if (match is { } duplicate)
            {
                var old = existing[duplicate.Entry.Code];
                entries.Add(new(entry.Code, KnowledgeChangeType.PossibleDuplicate, entry.Kind, entry.Code, entry.Title,
                    old.Item.Code, old.Entry.Title, old.Item.ContentHash, Math.Round(duplicate.Score, 2), DefaultSelected: false,
                    FieldChanges(old.Entry, entry), dataJson));
                continue;
            }

            entries.Add(new(entry.Code, KnowledgeChangeType.Added, entry.Kind, entry.Code, entry.Title,
                null, null, null, null, DefaultSelected: true, FieldChanges(null, entry), dataJson));
        }

        foreach (var (code, current) in existing.Where(e => !incomingCodes.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            entries.Add(new($"missing:{code}", KnowledgeChangeType.Missing, current.Item.Kind, code, current.Entry.Title,
                code, current.Entry.Title, current.Item.ContentHash, null, DefaultSelected: false,
                FieldChanges(current.Entry, null), null));
        }

        var summary = new KnowledgeDiffSummary(
            entries.Count(e => e.Type == KnowledgeChangeType.Added),
            entries.Count(e => e.Type == KnowledgeChangeType.Changed),
            entries.Count(e => e.Type == KnowledgeChangeType.PossibleDuplicate),
            entries.Count(e => e.Type == KnowledgeChangeType.Missing),
            unchanged);

        return new KnowledgeDiff(summary, entries);
    }

    /// <summary>Các trường khác nhau. old = null: mục mới (mọi trường là "mới"); new = null: mục sắp xóa.</summary>
    public static IReadOnlyList<KnowledgeFieldChange> FieldChanges(KnowledgeEntry? old, KnowledgeEntry? @new)
    {
        var kind = (old ?? @new)?.Kind ?? throw new ArgumentException("Cần ít nhất một mục.");
        var changes = new List<KnowledgeFieldChange>();
        foreach (var field in KnowledgeTemplate.For(kind).Fields)
        {
            if (field.Type == KnowledgeFieldType.Code)
            {
                continue;
            }

            var before = Display(old, field);
            var after = Display(@new, field);
            if (before != after)
            {
                changes.Add(new(field.JsonName, field.Header, FieldTypeName(field.Type), before, after));
            }
        }

        return changes;
    }

    private static string? Display(KnowledgeEntry? entry, KnowledgeField field) =>
        entry is not null && entry.Fields.TryGetValue(field.JsonName, out var value)
            ? value is long n ? n.ToString(CultureInfo.InvariantCulture) : (string)value
            : null;

    private static string FieldTypeName(KnowledgeFieldType type) => type switch
    {
        KnowledgeFieldType.Money => "money",
        KnowledgeFieldType.PositiveInt => "number",
        _ => "text",
    };

    private static (KnowledgeEntry Entry, double Score)? BestMatch(KnowledgeEntry entry, IEnumerable<KnowledgeEntry> candidates)
    {
        var target = TitleKey.From(entry.Title);
        (KnowledgeEntry Entry, double Score)? best = null;

        foreach (var candidate in candidates.Where(c => c.Kind == entry.Kind))
        {
            var score = target.Similarity(TitleKey.From(candidate.Title));
            if (score >= DuplicateThreshold && (best is null || score > best.Value.Score))
            {
                best = (candidate, score);
            }
        }

        return best;
    }

    /// <summary>Tên đã chuẩn hóa: chữ (bỏ dấu, chữ thường) và các con số tách riêng.</summary>
    private sealed record TitleKey(string Words, string Numbers)
    {
        public static TitleKey From(string title)
        {
            var decomposed = title.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
            var words = new StringBuilder();
            var numbers = new StringBuilder();
            var current = new StringBuilder();

            void Flush()
            {
                if (current.Length == 0)
                {
                    return;
                }

                var token = current.ToString();
                var target = char.IsDigit(token[0]) ? numbers : words;
                target.Append(target.Length > 0 ? " " : "").Append(token);
                current.Clear();
            }

            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch) && (current.Length == 0 || char.IsDigit(ch) == char.IsDigit(current[0])))
                {
                    current.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    Flush();
                    if (char.IsLetterOrDigit(ch))
                    {
                        current.Append(char.ToLowerInvariant(ch));
                    }
                }
            }

            Flush();
            return new TitleKey(words.ToString(), numbers.ToString());
        }

        /// <summary>0..1. Số khác nhau ("60 phút" vs "90 phút") → 0: đó là hai mục khác nhau.</summary>
        public double Similarity(TitleKey other)
        {
            if (Numbers != other.Numbers || Words.Length == 0 || other.Words.Length == 0)
            {
                return 0;
            }

            var distance = Levenshtein(Words, other.Words);
            return 1.0 - ((double)distance / Math.Max(Words.Length, other.Words.Length));
        }

        private static int Levenshtein(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
