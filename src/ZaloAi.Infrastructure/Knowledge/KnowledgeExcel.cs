using System.Globalization;
using ClosedXML.Excel;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Knowledge;

/// <summary>Đọc/ghi file Excel theo mẫu. Mỗi loại mục một sheet, dòng 1 là tiêu đề cột.</summary>
public static class KnowledgeExcel
{
    private static readonly XLColor RequiredHeaderColor = XLColor.FromHtml("#FDE68A");
    private static readonly XLColor HeaderColor = XLColor.FromHtml("#E5E7EB");

    public static KnowledgeParseResult Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
#pragma warning disable CA1031 // ClosedXML ném nhiều loại exception khác nhau cho file hỏng; mọi trường hợp đều là "file không đọc được".
        catch (Exception)
#pragma warning restore CA1031
        {
            return KnowledgeParseResult.FileError("Không đọc được file Excel. Hãy lưu lại dưới dạng .xlsx (Excel Workbook) rồi thử lại.");
        }

        using (workbook)
        {
            var collector = new KnowledgeEntryCollector();
            var foundAnySheet = false;

            foreach (var definition in KnowledgeTemplate.Kinds)
            {
                var sheet = workbook.Worksheets.FirstOrDefault(w => Normalize(w.Name) == Normalize(definition.SheetName));
                if (sheet is null)
                {
                    continue;
                }

                foundAnySheet = true;
                ReadSheet(sheet, definition, collector);
            }

            if (!foundAnySheet)
            {
                var expected = string.Join(", ", KnowledgeTemplate.Kinds.Select(k => $"\"{k.SheetName}\""));
                return KnowledgeParseResult.FileError($"Không thấy sheet nào theo mẫu ({expected}). Hãy dùng file mẫu tải từ hệ thống.");
            }

            return collector.Result();
        }
    }

    private static void ReadSheet(IXLWorksheet sheet, KnowledgeKindDefinition definition, KnowledgeEntryCollector collector)
    {
        // Ánh xạ tiêu đề cột → số cột. Không phân biệt hoa thường, bỏ dấu * và khoảng trắng thừa.
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var c = 1; c <= lastColumn; c++)
        {
            var header = Normalize(sheet.Cell(1, c).GetString());
            if (header.Length > 0)
            {
                columns.TryAdd(header, c);
            }
        }

        var missingRequired = false;
        foreach (var field in definition.Fields.Where(f => f.Required && !columns.ContainsKey(Normalize(f.Header))))
        {
            collector.AddError(new(definition.SheetName, 1, field.Header, "Thiếu cột bắt buộc. Không đổi tên tiêu đề cột của file mẫu."));
            missingRequired = true;
        }

        if (missingRequired)
        {
            return;
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= lastRow; row++)
        {
            var values = new Dictionary<KnowledgeField, object?>();
            foreach (var field in definition.Fields)
            {
                values[field] = columns.TryGetValue(Normalize(field.Header), out var column)
                    ? ReadCell(sheet.Cell(row, column))
                    : null;
            }

            // Dòng trống hoàn toàn: bỏ qua (người dùng hay để dòng trống giữa các nhóm).
            if (values.Values.All(v => v is null || (v is string s && string.IsNullOrWhiteSpace(s))))
            {
                continue;
            }

            collector.Add(definition, definition.SheetName, row, field => values[field]);
        }
    }

    private static object? ReadCell(IXLCell cell)
    {
        var value = cell.Value;
        return value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Number => value.GetNumber(),
            XLDataType.Text => value.GetText(),
            XLDataType.Boolean => value.GetBoolean() ? "TRUE" : "FALSE",
            XLDataType.DateTime => value.GetDateTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => value.GetTimeSpan().ToString(),
            _ => cell.GetFormattedString(),
        };
    }

    /// <summary>Tạo file Excel: không có mục nào = file mẫu trống; có mục = xuất dữ liệu hiện tại.</summary>
    public static void Write(IReadOnlyList<KnowledgeEntry> entries, Stream output)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(output);

        using var workbook = new XLWorkbook();
        WriteGuide(workbook.Worksheets.Add(KnowledgeTemplate.GuideSheetName));

        foreach (var definition in KnowledgeTemplate.Kinds)
        {
            var sheet = workbook.Worksheets.Add(definition.SheetName);
            for (var i = 0; i < definition.Fields.Count; i++)
            {
                var field = definition.Fields[i];
                var header = sheet.Cell(1, i + 1);
                header.Value = field.Header;
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = field.Required ? RequiredHeaderColor : HeaderColor;
                if (field.Required)
                {
                    header.CreateComment().AddText("Bắt buộc");
                }

                var column = sheet.Column(i + 1);
                switch (field.Type)
                {
                    case KnowledgeFieldType.Code:
                        column.Width = 28;
                        column.Style.NumberFormat.Format = "@";
                        break;
                    case KnowledgeFieldType.Money:
                        column.Width = 14;
                        column.Style.NumberFormat.Format = "#,##0";
                        break;
                    case KnowledgeFieldType.PositiveInt:
                        column.Width = 12;
                        break;
                    default:
                        column.Width = field.MaxLength > 500 ? 60 : 30;
                        column.Style.Alignment.WrapText = field.MaxLength > 500;
                        break;
                }
            }

            var row = 2;
            foreach (var entry in entries.Where(e => e.Kind == definition.Kind).OrderBy(e => e.Code, StringComparer.Ordinal))
            {
                for (var i = 0; i < definition.Fields.Count; i++)
                {
                    var cell = sheet.Cell(row, i + 1);
                    var field = definition.Fields[i];
                    if (field.Type == KnowledgeFieldType.Code)
                    {
                        cell.Value = entry.Code;
                    }
                    else if (entry.Fields.TryGetValue(field.JsonName, out var value))
                    {
                        cell.Value = value is long n ? n : (string)value;
                    }
                }

                row++;
            }

            sheet.SheetView.FreezeRows(1);
            sheet.Row(1).Height = 22;
        }

        workbook.SaveAs(output);
    }

    private static void WriteGuide(IXLWorksheet sheet)
    {
        string[] lines =
        [
            "HƯỚNG DẪN ĐIỀN MẪU DỮ LIỆU KIẾN THỨC",
            "",
            "1. Mỗi sheet là một loại thông tin: Thông tin chung, Dịch vụ, Gói liệu trình, Câu hỏi thường gặp, Chính sách. Mỗi dòng là một mục.",
            "2. Cột tô vàng là bắt buộc. Không đổi tên tiêu đề cột, không đổi tên sheet.",
            "3. Mã: chữ in hoa không dấu, số, dấu gạch ngang, 2–50 ký tự, không trùng nhau. Ví dụ: DV-MASSAGE-60, FAQ-GIO-MO-CUA.",
            "   Gợi ý tiền tố: TT- (thông tin chung), DV- (dịch vụ), GOI- (gói), FAQ- (câu hỏi), CS- (chính sách).",
            "4. GIỮ NGUYÊN MÃ khi sửa nội dung một mục. Đổi mã = hệ thống hiểu là mục mới.",
            "5. Giá: số tiền VNĐ, ví dụ 450000 hoặc 450.000. Không rõ giá thì để trống và ghi lý do vào cột Ghi chú giá. Không ghi \"liên hệ\" vào cột Giá.",
            "6. Mỗi mức thời lượng/giá khác nhau của cùng dịch vụ là một dòng riêng.",
            "7. Sheet này (Hướng dẫn) được bỏ qua khi nhập.",
            "",
            "Khi nhập file, hệ thống KHÔNG thay đổi dữ liệu ngay mà hiện bản so sánh (thêm mới, thay đổi, có thể trùng, không còn trong file) để bạn chọn trước khi áp dụng.",
            "",
            "Đặc tả đầy đủ và câu lệnh mẫu để nhờ AI (ChatGPT, Claude, Gemini) điền giúp: xem trang Kho kiến thức trên hệ thống.",
        ];

        for (var i = 0; i < lines.Length; i++)
        {
            sheet.Cell(i + 1, 1).Value = lines[i];
        }

        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Column(1).Width = 140;
    }

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Replace("*", "", StringComparison.Ordinal)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
}
