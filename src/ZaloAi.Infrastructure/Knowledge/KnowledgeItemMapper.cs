using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Chuyển giữa <see cref="KnowledgeEntry"/> (đọc từ file) và <see cref="KnowledgeItem"/> (lưu DB), và ghi file JSON.</summary>
public static class KnowledgeItemMapper
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    // Giữ nguyên chữ tiếng Việt trong JSON (không thành ạ...), để file xuất ra đọc được.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON gọn của mục, thứ tự trường theo mẫu → cùng nội dung luôn ra cùng chuỗi (dùng cho hash).</summary>
    public static string ToDataJson(KnowledgeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteEntry(writer, entry);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string ContentHash(KnowledgeEntry entry) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToDataJson(entry))));

    /// <summary>Văn bản để đánh chỉ mục và hiển thị: nhãn tiếng Việt + giá trị, giá định dạng "450.000 đ".</summary>
    public static string ToSearchText(KnowledgeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var definition = KnowledgeTemplate.For(entry.Kind);
        var titleField = KnowledgeTemplate.TitleField(entry.Kind);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"[{definition.Label}] {entry.Title}");

        foreach (var field in definition.Fields)
        {
            if (field.Type == KnowledgeFieldType.Code || field.JsonName == titleField
                || !entry.Fields.TryGetValue(field.JsonName, out var value))
            {
                continue;
            }

            var shown = value switch
            {
                long n when field.Type == KnowledgeFieldType.Money =>
                    n.ToString("#,##0", Vietnamese) + " đ" + (entry.Text("priceUnit") is { } unit ? $" / {unit}" : ""),
                long n => n.ToString(CultureInfo.InvariantCulture),
                _ => (string)value,
            };

            if (field.JsonName == "priceUnit")
            {
                continue; // đã ghép vào dòng Giá
            }

            text.Append('\n').Append(field.Header).Append(": ").Append(shown);
        }

        return text.ToString();
    }

    public static KnowledgeItem ToItem(KnowledgeEntry entry, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new KnowledgeItem
        {
            Kind = entry.Kind,
            Code = entry.Code,
            DataJson = ToDataJson(entry),
            SearchText = ToSearchText(entry),
            ContentHash = ContentHash(entry),
            UpdatedBy = updatedBy,
        };
    }

    /// <summary>Ghi đè nội dung mục đang có bằng nội dung mới (giữ Id, Mã).</summary>
    public static void Apply(KnowledgeItem item, KnowledgeEntry entry, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(entry);
        item.Kind = entry.Kind;
        item.DataJson = ToDataJson(entry);
        item.SearchText = ToSearchText(entry);
        item.ContentHash = ContentHash(entry);
        item.UpdatedBy = updatedBy;
    }

    public static KnowledgeEntry FromItem(KnowledgeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var definition = KnowledgeTemplate.For(item.Kind);
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(item.DataJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (definition.FindByJsonName(property.Name) is not { } field)
            {
                continue;
            }

            fields[field.JsonName] = property.Value.ValueKind == JsonValueKind.Number
                ? property.Value.GetInt64()
                : property.Value.GetString() ?? "";
        }

        fields["code"] = item.Code;
        return new KnowledgeEntry(item.Kind, item.Code, fields);
    }

    /// <summary>File JSON đầy đủ theo mẫu (xuất dữ liệu, hoặc đưa cho AI sửa).</summary>
    public static void WriteJsonFile(IReadOnlyList<KnowledgeEntry> entries, Stream output)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var writer = new Utf8JsonWriter(output, WriterOptions with { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("format", KnowledgeTemplate.FormatName);
        writer.WriteNumber("version", KnowledgeTemplate.FormatVersion);
        foreach (var definition in KnowledgeTemplate.Kinds)
        {
            writer.WriteStartArray(definition.JsonKey);
            foreach (var entry in entries.Where(e => e.Kind == definition.Kind).OrderBy(e => e.Code, StringComparer.Ordinal))
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, KnowledgeEntry entry)
    {
        writer.WriteStartObject();
        foreach (var field in KnowledgeTemplate.For(entry.Kind).Fields)
        {
            if (field.Type == KnowledgeFieldType.Code)
            {
                writer.WriteString(field.JsonName, entry.Code);
            }
            else if (entry.Fields.TryGetValue(field.JsonName, out var value))
            {
                if (value is long n)
                {
                    writer.WriteNumber(field.JsonName, n);
                }
                else
                {
                    writer.WriteString(field.JsonName, (string)value);
                }
            }
        }

        writer.WriteEndObject();
    }
}
