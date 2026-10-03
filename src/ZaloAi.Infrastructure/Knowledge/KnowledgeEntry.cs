using System.Globalization;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>
/// Một mục đã đọc và kiểm tra xong. <see cref="Fields"/> chỉ chứa trường có giá trị
/// (chuỗi đã trim cho trường chữ, <see cref="long"/> cho trường số), khóa là tên trường JSON.
/// </summary>
public sealed record KnowledgeEntry(KnowledgeKind Kind, string Code, IReadOnlyDictionary<string, object> Fields)
{
    public string? Text(string field) => Fields.TryGetValue(field, out var v) ? v as string : null;

    public long? Number(string field) => Fields.TryGetValue(field, out var v) && v is long n ? n : null;

    /// <summary>Tên hiển thị: tên dịch vụ, câu hỏi, tiêu đề...</summary>
    public string Title => Text(KnowledgeTemplate.TitleField(Kind)) ?? Code;
}

/// <summary>Lỗi định dạng, chỉ rõ vị trí để người dùng sửa file.</summary>
/// <param name="Location">Tên sheet (Excel) hoặc nhóm (JSON), ví dụ "Dịch vụ".</param>
/// <param name="Row">Dòng Excel (tính cả dòng tiêu đề) hoặc thứ tự mục trong JSON (từ 1). Null = lỗi chung của file.</param>
/// <param name="Column">Tên cột, null nếu lỗi cả dòng.</param>
public sealed record KnowledgeParseError(string Location, int? Row, string? Column, string Message);

public sealed record KnowledgeParseResult(IReadOnlyList<KnowledgeEntry> Entries, IReadOnlyList<KnowledgeParseError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static KnowledgeParseResult FileError(string message) =>
        new([], [new KnowledgeParseError("File", null, null, message)]);
}

/// <summary>Kiểm tra và chuẩn hóa từng ô, gom lỗi; dùng chung cho đọc Excel và JSON để báo lỗi giống nhau.</summary>
internal sealed class KnowledgeEntryCollector
{
    private const int MaxErrors = 200;

    private readonly Dictionary<string, string> _seenCodes = new(StringComparer.Ordinal);
    private readonly List<KnowledgeEntry> _entries = [];
    private readonly List<KnowledgeParseError> _errors = [];
    private bool _tooManyEntriesReported;

    public KnowledgeParseResult Result() => new(_entries, _errors);

    public void AddError(KnowledgeParseError error)
    {
        if (_errors.Count < MaxErrors)
        {
            _errors.Add(error);
        }
    }

    /// <param name="read">Trả giá trị thô của một trường: null, <see cref="string"/> hoặc <see cref="double"/>.</param>
    public void Add(KnowledgeKindDefinition definition, string location, int row, Func<KnowledgeField, object?> read)
    {
        if (_entries.Count >= KnowledgeTemplate.MaxEntries)
        {
            if (!_tooManyEntriesReported)
            {
                AddError(new(location, row, null, $"File có quá {KnowledgeTemplate.MaxEntries} mục. Chia thành nhiều file nhỏ hơn."));
                _tooManyEntriesReported = true;
            }

            return;
        }

        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        var valid = true;
        string? code = null;

        foreach (var field in definition.Fields)
        {
            var (value, error) = Convert(field, read(field));
            if (error is not null)
            {
                AddError(new(location, row, field.Header, error));
                valid = false;
                continue;
            }

            if (value is null)
            {
                if (field.Required)
                {
                    AddError(new(location, row, field.Header, "Bắt buộc, không được để trống."));
                    valid = false;
                }

                continue;
            }

            if (field.Type == KnowledgeFieldType.Code)
            {
                code = (string)value;
            }

            fields[field.JsonName] = value;
        }

        if (!valid || code is null)
        {
            return;
        }

        var here = $"{location}, dòng {row}";
        if (_seenCodes.TryGetValue(code, out var firstSeen))
        {
            AddError(new(location, row, "Mã", $"Mã \"{code}\" bị trùng với {firstSeen}. Mỗi mục phải có mã riêng."));
            return;
        }

        _seenCodes[code] = here;
        _entries.Add(new KnowledgeEntry(definition.Kind, code, fields));
    }

    private static (object? Value, string? Error) Convert(KnowledgeField field, object? raw)
    {
        var text = raw switch
        {
            null => null,
            string s => s,
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => raw.ToString(),
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        switch (field.Type)
        {
            case KnowledgeFieldType.Code:
                return KnowledgeTemplate.NormalizeCode(text) is { } code
                    ? (code, null)
                    : (null, "Mã chỉ gồm chữ in hoa không dấu (A–Z), số, dấu gạch ngang, dài 2–50 ký tự. Ví dụ: DV-MASSAGE-60.");

            case KnowledgeFieldType.Text:
                var value = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
                return field.MaxLength > 0 && value.Length > field.MaxLength
                    ? (null, $"Dài quá {field.MaxLength} ký tự (đang có {value.Length}).")
                    : (value, null);

            case KnowledgeFieldType.PositiveInt:
                return TryWholeNumber(raw, text, out var number) && number > 0
                    ? (number, null)
                    : (null, "Phải là số nguyên lớn hơn 0.");

            case KnowledgeFieldType.Money:
                return TryMoney(raw, text, out var money)
                    ? (money, null)
                    : (null, "Giá phải là số tiền VNĐ, ví dụ 450000 hoặc 450.000đ. Không rõ giá thì để trống và ghi vào cột Ghi chú giá.");

            default:
                throw new InvalidOperationException($"Kiểu trường chưa hỗ trợ: {field.Type}");
        }
    }

    private static bool TryWholeNumber(object? raw, string text, out long value)
    {
        if (raw is double d)
        {
            value = (long)d;
            return d == Math.Floor(d) && d is >= long.MinValue and <= long.MaxValue;
        }

        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Chấp nhận 450000, "450000", "450.000", "450,000", "450.000đ", "450.000 VNĐ".</summary>
    private static bool TryMoney(object? raw, string text, out long value)
    {
        if (raw is double)
        {
            return TryWholeNumber(raw, text, out value) && value >= 0;
        }

        var cleaned = text.Trim().ToLowerInvariant();
        foreach (var suffix in new[] { "vnđ", "vnd", "đồng", "đ", "₫" })
        {
            cleaned = cleaned.Replace(suffix, "", StringComparison.Ordinal);
        }

        cleaned = cleaned.Replace(" ", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .Replace(",", "", StringComparison.Ordinal);

        return long.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out value) && cleaned.Length > 0;
    }
}
