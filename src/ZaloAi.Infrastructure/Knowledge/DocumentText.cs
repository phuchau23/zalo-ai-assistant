using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Lỗi đọc nội dung tài liệu (file hỏng, PDF dạng ảnh...). Message an toàn để hiện cho người dùng; không nên thử lại.</summary>
public sealed class DocumentReadException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Lấy chữ từ tài liệu tự do. Tiêu đề mục được giữ dạng markdown ("# ...") để bộ chia đoạn biết đoạn thuộc mục nào.
/// </summary>
public static class DocumentText
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".txt", ".md",
    };

    public static string Extract(string fileName, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            var text = extension switch
            {
                ".pdf" => Pdf(content),
                ".docx" => Word(content),
                ".xlsx" => Excel(content),
                ".txt" or ".md" => PlainText(content),
                _ => throw new DocumentReadException("Chỉ hỗ trợ PDF, Word (.docx), Excel (.xlsx), TXT, MD."),
            };

            var cleaned = Clean(text);
            if (cleaned.Length < 20)
            {
                throw new DocumentReadException(extension == ".pdf"
                    ? "Không đọc được chữ trong PDF — có thể là ảnh scan. Hãy dùng PDF có chữ chọn được, hoặc gõ lại nội dung vào Word."
                    : "Tài liệu gần như không có chữ.");
            }

            return cleaned;
        }
        catch (DocumentReadException)
        {
            throw;
        }
#pragma warning disable CA1031 // Thư viện đọc file ném nhiều loại exception khác nhau cho file hỏng.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new DocumentReadException("File bị hỏng hoặc không đúng định dạng, không đọc được nội dung.", ex);
        }
    }

    private static string Pdf(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        using var pdf = PdfDocument.Open(buffer.ToArray());
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            text.AppendLine(ContentOrderTextExtractor.GetText(page)).AppendLine();
        }

        return text.ToString();
    }

    private static string Word(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        using var document = WordprocessingDocument.Open(buffer, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return "";
        }

        var text = new StringBuilder();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    var line = paragraph.InnerText.Trim();
                    if (line.Length == 0)
                    {
                        text.AppendLine();
                        break;
                    }

                    var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                    var isHeading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                        || style.StartsWith("Title", StringComparison.OrdinalIgnoreCase);
                    text.AppendLine(isHeading ? $"# {line}" : line);
                    if (isHeading)
                    {
                        text.AppendLine();
                    }

                    break;

                case Table table:
                    foreach (var row in table.Elements<TableRow>())
                    {
                        text.AppendLine(string.Join(" | ", row.Elements<TableCell>().Select(c => c.InnerText.Trim())));
                    }

                    text.AppendLine();
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Mỗi sheet là một mục; mỗi dòng ghi "Tiêu đề cột: giá trị" để câu trả lời giữ được ngữ cảnh.</summary>
    private static string Excel(Stream content)
    {
        using var workbook = new XLWorkbook(content);
        var text = new StringBuilder();
        foreach (var sheet in workbook.Worksheets)
        {
            var range = sheet.RangeUsed();
            if (range is null)
            {
                continue;
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"# {sheet.Name}").AppendLine();
            var rows = range.RowsUsed().ToList();
            var headers = rows[0].Cells().Select(c => c.GetFormattedString().Trim()).ToList();
            foreach (var row in rows.Skip(1))
            {
                var cells = row.Cells().Select(c => c.GetFormattedString().Trim()).ToList();
                var parts = cells
                    .Select((value, i) => (Header: i < headers.Count ? headers[i] : "", Value: value))
                    .Where(p => p.Value.Length > 0)
                    .Select(p => p.Header.Length > 0 ? $"{p.Header}: {p.Value}" : p.Value);
                text.AppendLine(string.Join("; ", parts)).AppendLine();
            }
        }

        return text.ToString();
    }

    private static string PlainText(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Chuẩn hóa xuống dòng, bỏ khoảng trắng thừa, gộp nhiều dòng trống thành một.</summary>
    private static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        var result = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank = result.Length > 0;
                continue;
            }

            if (blank)
            {
                result.Append('\n');
                blank = false;
            }

            result.Append(line).Append('\n');
        }

        return result.ToString().Trim();
    }
}

/// <summary>Một đoạn sau khi chia: thuộc mục nào (tiêu đề gần nhất) và nội dung.</summary>
public sealed record TextChunk(int Ordinal, string? Section, string Content);

/// <summary>
/// Chia tài liệu thành đoạn ~1.500 ký tự (~500 token tiếng Việt), chồng lấn ~15% để câu ở ranh giới không bị cắt mất ý,
/// không vượt qua tiêu đề mục (đoạn mới bắt đầu ở mỗi mục mới).
/// </summary>
public static class TextChunker
{
    public const int MaxChars = 1500;
    public const int OverlapChars = 220;

    public static IReadOnlyList<TextChunk> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunks = new List<TextChunk>();
        string? section = null;
        var current = new StringBuilder();

        void Emit()
        {
            var content = current.ToString().Trim();
            if (content.Length > 0)
            {
                chunks.Add(new TextChunk(chunks.Count, section, content));
            }

            current.Clear();
        }

        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (paragraph.StartsWith('#'))
            {
                Emit();
                var heading = paragraph.Split('\n')[0];
                section = heading.TrimStart('#').Trim();
                var rest = paragraph[heading.Length..].Trim();
                if (rest.Length > 0)
                {
                    current.Append(rest).Append("\n\n");
                }

                continue;
            }

            foreach (var piece in SplitLong(paragraph))
            {
                if (current.Length > 0 && current.Length + piece.Length > MaxChars)
                {
                    var tail = Tail(current.ToString());
                    Emit();
                    current.Append(tail);
                }

                current.Append(piece).Append("\n\n");
            }
        }

        Emit();
        return chunks;
    }

    /// <summary>Đoạn văn dài hơn giới hạn: cắt theo câu, câu quá dài thì cắt cứng.</summary>
    private static IEnumerable<string> SplitLong(string paragraph)
    {
        if (paragraph.Length <= MaxChars)
        {
            yield return paragraph;
            yield break;
        }

        var buffer = new StringBuilder();
        foreach (var sentence in paragraph.Split(". ", StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = sentence.EndsWith('.') ? sentence : sentence + ".";
            if (buffer.Length + piece.Length + 1 > MaxChars && buffer.Length > 0)
            {
                yield return buffer.ToString().Trim();
                buffer.Clear();
            }

            for (var i = 0; i < piece.Length; i += MaxChars)
            {
                buffer.Append(piece.AsSpan(i, Math.Min(MaxChars, piece.Length - i))).Append(' ');
                if (buffer.Length >= MaxChars)
                {
                    yield return buffer.ToString().Trim();
                    buffer.Clear();
                }
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString().Trim();
        }
    }

    /// <summary>Phần cuối đoạn trước (bắt đầu ở ranh giới từ) để chồng lấn sang đoạn sau.</summary>
    private static string Tail(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length <= OverlapChars)
        {
            return "";
        }

        var start = trimmed.IndexOf(' ', trimmed.Length - OverlapChars);
        return start < 0 ? "" : trimmed[(start + 1)..] + "\n\n";
    }
}
