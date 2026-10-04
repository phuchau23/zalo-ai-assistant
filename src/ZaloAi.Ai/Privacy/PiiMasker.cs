using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZaloAi.Ai.Privacy;

/// <summary>
/// Bảng tra giữa placeholder và giá trị gốc trong MỘT lượt xử lý (cùng giá trị → cùng placeholder qua mọi tin của hội thoại).
/// Chỉ nằm trong bộ nhớ, không lưu, không log.
/// </summary>
public sealed class PiiVault
{
    private readonly Dictionary<string, string> _byValue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly List<(string Kind, string Value)> _known = [];

    public int Count => _byPlaceholder.Count;

    /// <summary>Giá trị đã biết (tên khách, địa chỉ khách tự khai) — che đúng chuỗi này ở mọi nơi.</summary>
    public void AddKnown(string kind, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 2)
        {
            _known.Add((kind, value.Trim()));
        }
    }

    internal IReadOnlyList<(string Kind, string Value)> Known => _known;

    internal string PlaceholderFor(string kind, string value)
    {
        if (_byValue.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var n = _counters.GetValueOrDefault(kind) + 1;
        _counters[kind] = n;
        var placeholder = $"[{kind}_{n.ToString(CultureInfo.InvariantCulture)}]";
        _byValue[value] = placeholder;
        _byPlaceholder[placeholder] = value;
        return placeholder;
    }

    internal bool TryGetOriginal(string placeholder, out string value) => _byPlaceholder.TryGetValue(placeholder, out value!);
}

/// <summary>
/// Che dữ liệu cá nhân trước khi gửi nội dung cho AI (CLAUDE.md mục 7): số điện thoại VN, email, CCCD (12 số), CMND (9 số, chỉ khi
/// có chữ "CMND/chứng minh" gần đó để không che nhầm giá tiền), tên/địa chỉ đã biết. AI thấy "[PHONE_1]" và được dặn giữ nguyên;
/// <see cref="Restore"/> ghép lại giá trị gốc vào câu trả lời trước khi gửi khách.
/// </summary>
public static partial class PiiMasker
{
    public const string Phone = "PHONE";
    public const string Email = "EMAIL";
    public const string IdNumber = "ID";
    public const string Name = "NAME";
    public const string Address = "ADDRESS";

    public static string Mask(string text, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = EmailPattern().Replace(text, m => vault.PlaceholderFor(Email, m.Value));
        result = DigitRun().Replace(result, m => MaskDigits(m, result, vault));
        foreach (var (kind, value) in vault.Known.OrderByDescending(k => k.Value.Length))
        {
            result = Regex.Replace(
                result,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}])",
                _ => vault.PlaceholderFor(kind, value),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        return result;
    }

    /// <summary>Thay placeholder bằng giá trị gốc. Placeholder lạ (AI tự bịa) bị bỏ đi thay vì gửi nguyên cho khách.</summary>
    public static string Restore(string text, PiiVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return PlaceholderPattern().Replace(text, m => vault.TryGetOriginal(m.Value, out var original) ? original : "");
    }

    public static bool ContainsPlaceholder(string text) => PlaceholderPattern().IsMatch(text);

    private static string MaskDigits(Match match, string source, PiiVault vault)
    {
        var raw = match.Value;
        var digits = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
        }

        var d = digits.ToString();
        var contiguous = d.Length == raw.Length;

        if (IsVietnamesePhone(d, raw))
        {
            return vault.PlaceholderFor(Phone, raw);
        }

        // CCCD: 12 số liền, bắt đầu bằng mã tỉnh 0xx.
        if (contiguous && d.Length == 12 && d[0] == '0')
        {
            return vault.PlaceholderFor(IdNumber, raw);
        }

        // CMND cũ 9 số: chỉ khi ngay trước có từ khóa (tránh che nhầm "450000000đ").
        if (contiguous && d.Length == 9)
        {
            var before = source[Math.Max(0, match.Index - 25)..match.Index];
            if (IdKeyword().IsMatch(before))
            {
                return vault.PlaceholderFor(IdNumber, raw);
            }
        }

        return raw;
    }

    private static bool IsVietnamesePhone(string d, string raw)
    {
        if (raw.StartsWith('+') && !d.StartsWith("84", StringComparison.Ordinal))
        {
            return false;
        }

        var national = d.StartsWith("84", StringComparison.Ordinal) && d.Length is 11 or 12 ? "0" + d[2..] : d;
        return national.Length switch
        {
            10 => national[0] == '0' && national[1] is '3' or '5' or '7' or '8' or '9', // di động
            11 => national.StartsWith("02", StringComparison.Ordinal), // cố định 02x
            _ => false,
        };
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EmailPattern();

    /// <summary>Dãy số (có thể có dấu cách, chấm, gạch đơn giữa các số), 9–14 chữ số.</summary>
    [GeneratedRegex(@"(?<![\d\w.])\+?\d(?:[ .\-]?\d){8,13}(?![\d\w])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"(cmnd|cmt|ch[uứ]ng\s*minh|c[aă]n\s*c[uư][oớ]c|cccd|s[oố]\s*gi[aấ]y\s*t[oờ])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex IdKeyword();

    [GeneratedRegex(@"\[(?:PHONE|EMAIL|ID|NAME|ADDRESS)_\d+\]", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PlaceholderPattern();
}
