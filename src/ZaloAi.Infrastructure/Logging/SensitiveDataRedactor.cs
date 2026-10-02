using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace ZaloAi.Infrastructure.Logging;

/// <summary>
/// Che dữ liệu nhạy cảm trong log trước khi ghi ra sink:
/// 1. Property có tên nhạy cảm (token, password, phone, content...) → "***", kể cả trong object lồng nhau.
/// 2. Giá trị chuỗi khác: che SĐT Việt Nam và email.
/// Chỉ là lớp phòng thủ thứ hai: code vẫn không được chủ động log token/PII.
/// </summary>
public sealed partial class SensitiveDataRedactor : ILogEventEnricher
{
    public const string Mask = "***";

    // So khớp "chứa" (không phân biệt hoa thường), sau khi bỏ "_" và "-".
    private static readonly string[] SensitiveNameParts =
    [
        "token", "password", "secret", "apikey", "authorization", "cookie", "phone", "encryptionkey", "connectionstring",
    ];

    // So khớp nguyên tên, vì các từ này quá phổ biến để so "chứa" (ví dụ ContentType).
    private static readonly HashSet<string> SensitiveExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "text", "body", "email", "messagetext",
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = Redact(name, value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    public static bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return SensitiveExactNames.Contains(normalized)
            || SensitiveNameParts.Any(p => normalized.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    public static string MaskText(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var masked = EmailRegex().Replace(input, Mask);
        return VietnamPhoneRegex().Replace(masked, Mask);
    }

    private static LogEventPropertyValue Redact(string name, LogEventPropertyValue value) =>
        IsSensitiveName(name) ? new ScalarValue(Mask) : RedactValue(value);

    private static LogEventPropertyValue RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string s }:
                var masked = MaskText(s);
                return masked == s ? value : new ScalarValue(masked);

            case StructureValue structure:
                var props = structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Name, p.Value))).ToList();
                return new StructureValue(props, structure.TypeTag);

            case SequenceValue sequence:
                return new SequenceValue(sequence.Elements.Select(RedactValue));

            case DictionaryValue dictionary:
                return new DictionaryValue(dictionary.Elements.Select(kv =>
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                        kv.Key,
                        kv.Key.Value is string key ? Redact(key, kv.Value) : RedactValue(kv.Value))));

            default:
                return value;
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailRegex();

    // 0xxxxxxxxx hoặc +84/84xxxxxxxxx, cho phép dấu cách, chấm, gạch giữa các số.
    [GeneratedRegex(@"(?<!\d)(?:\+?84|0)(?:[\s.-]?\d){9}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex VietnamPhoneRegex();
}
