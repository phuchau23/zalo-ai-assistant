using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZaloAi.IndustryTemplates;

namespace ZaloAi.Ai.Safety;

/// <summary>Chuẩn hóa tiếng Việt để so khớp: chữ thường, bỏ dấu (đ → d), mọi ký tự không phải chữ/số thành một dấu cách.</summary>
public static class VietText
{
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 2);
        builder.Append(' ');
        var lastSpace = true;
        foreach (var ch in text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch == '%')
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                builder.Append(' ');
                lastSpace = true;
            }
        }

        if (!lastSpace)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    /// <summary>Cụm <paramref name="phrase"/> xuất hiện nguyên vẹn (theo từ) trong <paramref name="text"/>, bỏ dấu.</summary>
    public static bool ContainsFolded(string foldedText, string phrase) => foldedText.Contains(Fold(phrase), StringComparison.Ordinal);
}

/// <summary>Kiểm tra dấu hiệu nguy hiểm TRƯỚC khi gọi AI (INDUSTRIES.md mục 3) — so khớp bỏ dấu vì khách hay gõ không dấu.</summary>
public static class DangerDetector
{
    /// <returns>Tên dấu hiệu đầu tiên khớp, null nếu không có.</returns>
    public static string? Detect(IndustryTemplate template, string customerMessage)
    {
        ArgumentNullException.ThrowIfNull(template);
        var folded = VietText.Fold(customerMessage ?? "");
        foreach (var signal in template.DangerSignals)
        {
            if (signal.Keywords.Any(k => VietText.ContainsFolded(folded, k)))
            {
                return signal.Name;
            }
        }

        return null;
    }
}

/// <summary>
/// Lọc câu cấm trên câu trả lời trước khi gửi (CLAUDE.md M3). So khớp CÓ dấu (bỏ dấu dễ bắt nhầm: "không đau" ≠ "không đâu").
/// Cụm không strict được phép nếu đúng nguyên văn có trong đoạn dữ liệu bot đã dùng (chính sách DN tự nạp).
/// </summary>
public static class ForbiddenFilter
{
    public static IReadOnlyList<string> FindViolations(IndustryTemplate template, string reply, IEnumerable<string> usedSourceTexts)
    {
        ArgumentNullException.ThrowIfNull(template);
        var text = Normalize(reply ?? "");
        var sources = Normalize(string.Join("\n", usedSourceTexts ?? []));
        var violations = new List<string>();
        foreach (var phrase in template.Forbidden)
        {
            var pattern = Pattern(phrase.Phrase);
            if (!pattern.IsMatch(text))
            {
                continue;
            }

            if (!phrase.Strict && pattern.IsMatch(sources))
            {
                continue;
            }

            violations.Add(phrase.Phrase);
        }

        return violations;
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static Regex Pattern(string phrase)
    {
        var escaped = Regex.Escape(Normalize(phrase)).Replace(@"\ ", @"\s+", StringComparison.Ordinal);
        return new Regex($@"(?<![\p{{L}}\p{{N}}]){escaped}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

/// <summary>Phát hiện câu trả lời có con số giá tiền — dùng chặn "bịa giá" khi bot không dẫn đoạn dữ liệu nào.</summary>
public static partial class PriceDetector
{
    public static bool MentionsPrice(string text) => Price().IsMatch(text ?? "");

    [GeneratedRegex(@"\d[\d.,\s]*\s*(k(?!\p{L})|vnđ|vnd|đồng|nghìn|ngàn|tr(?!\p{L})|triệu|trieu|usd|đ(?!\p{L})|\$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Price();
}
