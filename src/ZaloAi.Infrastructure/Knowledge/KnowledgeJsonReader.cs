using System.Text.Json;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Đọc file JSON theo mẫu (thường do AI bên ngoài tạo). Dễ dãi với lỗi nhỏ: dấu phẩy thừa, chú thích, giá dạng chữ.</summary>
public static class KnowledgeJsonReader
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    public static KnowledgeParseResult Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream, Options);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (gần dòng {line + 1})" : "";
            return KnowledgeParseResult.FileError($"File JSON bị lỗi cú pháp{where}. Kiểm tra dấu ngoặc, dấu phẩy, dấu nháy.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return KnowledgeParseResult.FileError("File JSON phải là một object { ... } theo mẫu.");
            }

            if (TryGet(root, "format", out var format)
                && (format.ValueKind != JsonValueKind.String || format.GetString() != KnowledgeTemplate.FormatName))
            {
                return KnowledgeParseResult.FileError($"Trường \"format\" phải là \"{KnowledgeTemplate.FormatName}\".");
            }

            if (TryGet(root, "version", out var version)
                && (!version.TryGetInt32(out var v) || v > KnowledgeTemplate.FormatVersion))
            {
                return KnowledgeParseResult.FileError($"Hệ thống chỉ đọc được mẫu phiên bản {KnowledgeTemplate.FormatVersion}.");
            }

            var collector = new KnowledgeEntryCollector();
            foreach (var definition in KnowledgeTemplate.Kinds)
            {
                if (!TryGet(root, definition.JsonKey, out var array) || array.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (array.ValueKind != JsonValueKind.Array)
                {
                    collector.AddError(new(definition.Label, null, null, $"\"{definition.JsonKey}\" phải là danh sách [ ... ]."));
                    continue;
                }

                var index = 0;
                foreach (var element in array.EnumerateArray())
                {
                    index++;
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        collector.AddError(new(definition.Label, index, null, "Mỗi mục phải là một object { ... }."));
                        continue;
                    }

                    collector.Add(definition, definition.Label, index, field => ReadValue(element, field, collector, definition.Label, index));
                }
            }

            return collector.Result();
        }
    }

    private static object? ReadValue(JsonElement element, KnowledgeField field, KnowledgeEntryCollector collector, string location, int index)
    {
        if (!TryGet(element, field.JsonName, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.GetDouble();
            default:
                collector.AddError(new(location, index, field.Header, $"\"{field.JsonName}\" phải là chữ hoặc số."));
                return null;
        }
    }

    // Không phân biệt hoa thường tên trường: AI hay viết "Code", "Price".
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
