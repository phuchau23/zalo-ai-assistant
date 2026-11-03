using System.Text.Json;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Đọc JSON model trả về, dùng chung cho mọi nhà cung cấp: sửa nhẹ (bóc ```json, cắt chữ thừa ngoài {…}) rồi kiểm tra theo JSON Schema.
/// Chỉ hỗ trợ phần schema dự án đang dùng: type (một kiểu hoặc mảng kiểu), enum, properties, required, items. Từ khóa khác bỏ qua;
/// trường thừa không tính là lỗi (code đọc kết quả bỏ qua trường lạ).
/// </summary>
public static class JsonOutput
{
    /// <summary>Bóc ```json … ``` và chữ thừa trước/sau object. Không thấy object → trả lại chuỗi đã trim.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = s.IndexOf('\n', StringComparison.Ordinal);
            s = newline < 0 ? s.Trim('`') : s[(newline + 1)..];
            if (s.EndsWith("```", StringComparison.Ordinal))
            {
                s = s[..^3];
            }

            s = s.Trim();
        }

        var start = s.IndexOf('{', StringComparison.Ordinal);
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s;
    }

    /// <summary>
    /// Kết quả dùng được theo schema: chuỗi gốc nếu đã đúng, chuỗi đã sửa nhẹ nếu sửa xong thì đúng; null nếu vẫn sai.
    /// </summary>
    public static string? Validate(string text, string jsonSchema)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var schema = JsonDocument.Parse(jsonSchema);
        if (Matches(text, schema.RootElement))
        {
            return text;
        }

        var cleaned = Clean(text);
        return cleaned != text && Matches(cleaned, schema.RootElement) ? cleaned : null;
    }

    private static bool Matches(string text, JsonElement schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return Matches(doc.RootElement, schema);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool Matches(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(value, type))
        {
            return false;
        }

        if (schema.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array
            && !options.EnumerateArray().Any(option => SameValue(option, value)))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
                && required.EnumerateArray().Any(name => name.ValueKind == JsonValueKind.String && !value.TryGetProperty(name.GetString()!, out _)))
            {
                return false;
            }

            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (value.TryGetProperty(property.Name, out var child) && !Matches(child, property.Value))
                    {
                        return false;
                    }
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            return value.EnumerateArray().All(item => Matches(item, items));
        }

        return true;
    }

    private static bool TypeMatches(JsonElement value, JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => IsType(value, type.GetString()),
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && IsType(value, t.GetString())),
        _ => true,
    };

    private static bool IsType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => true,
    };

    private static bool SameValue(JsonElement a, JsonElement b) => (a.ValueKind, b.ValueKind) switch
    {
        (JsonValueKind.String, JsonValueKind.String) => a.GetString() == b.GetString(),
        (JsonValueKind.Number, JsonValueKind.Number) => a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) && x == y,
        (JsonValueKind.True, JsonValueKind.True) or (JsonValueKind.False, JsonValueKind.False) or (JsonValueKind.Null, JsonValueKind.Null) => true,
        _ => false,
    };
}
