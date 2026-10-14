using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Customers;

/// <param name="Row">Số dòng trong Excel (dòng 2 là khách đầu tiên).</param>
/// <param name="Phone">SĐT đã chuẩn hóa ("0xxxxxxxxx"); null khi sai.</param>
/// <param name="Salutation">anh | chị | cô | chú | bác — từ cột "Cách gọi", hoặc suy từ "Giới tính" (Nam → anh, Nữ → chị).</param>
/// <param name="Consent">Đồng ý nhận tin ưu đãi: true/false; null = để trống (giữ nguyên).</param>
public sealed record ContactImportRow(
    int Row,
    string? Phone,
    string? Name,
    string? Salutation,
    string? Service,
    DateOnly? ServiceDate,
    DateTimeOffset? FollowUpAt,
    string? AiNote,
    bool? Consent,
    IReadOnlyList<string> Errors)
{
    public bool Valid => Errors.Count == 0;
}

/// <summary>
/// File Excel danh sách khách của doanh nghiệp (Khách hàng → Nhập danh sách). Sheet "Khách hàng", một dòng một khách; cột đọc theo
/// tiêu đề (không theo vị trí). Ngày giờ hiểu theo giờ Việt Nam. Chỉ đọc, không ghi database.
/// </summary>
public static class ContactImportExcel
{
    public const string SheetName = "Khách hàng";
    public const int MaxRows = 5000;
    public const long MaxFileBytes = 5 * 1024 * 1024;

    public const string PhoneHeader = "SĐT";
    public const string NameHeader = "Tên";
    public const string GenderHeader = "Giới tính";
    public const string SalutationHeader = "Cách gọi";
    public const string ServiceHeader = "Đã làm gì";
    public const string ServiceDateHeader = "Ngày làm";
    public const string FollowUpHeader = "Hẹn nhắn lúc";
    public const string NoteHeader = "Ghi chú cho AI";
    public const string ConsentHeader = "Đồng ý nhận tin ưu đãi";

    private static readonly string[] Headers =
        [PhoneHeader, NameHeader, GenderHeader, SalutationHeader, ServiceHeader, ServiceDateHeader, FollowUpHeader, NoteHeader, ConsentHeader];

    private static readonly TimeSpan Vietnam = TimeSpan.FromHours(7);

    private static readonly string[] DateFormats = ["d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "yyyy-MM-dd"];

    private static readonly string[] DateTimeFormats =
        ["d/M/yyyy H:mm", "dd/MM/yyyy HH:mm", "d/M/yyyy H'h'mm", "d/M/yyyy H'h'", "yyyy-MM-dd HH:mm", "d/M/yyyy"];

    public static void WriteTemplate(Stream output)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = i == 0 ? XLColor.FromHtml("#FDE68A") : XLColor.FromHtml("#E5E7EB");
        }

        string[][] samples =
        [
            ["0912345678", "Nguyễn Thị Lan", "Nữ", "", "Gói bấm huyệt 10 buổi (buổi 1)", "10/10/2026", "17/10/2026 09:00", "Hay đau vai gáy", "Có"],
            ["0987654321", "Trần Văn Hùng", "Nam", "", "Massage body 60 phút", "12/10/2026", "15/10/2026 18:00", "", "Không"],
        ];
        for (var r = 0; r < samples.Length; r++)
        {
            for (var c = 0; c < samples[r].Length; c++)
            {
                var cell = sheet.Cell(r + 2, c + 1);
                cell.Style.NumberFormat.Format = "@";
                cell.Value = samples[r][c];
            }
        }

        sheet.Column(1).Style.NumberFormat.Format = "@"; // giữ số 0 đầu của SĐT
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        var guide = workbook.Worksheets.Add("Hướng dẫn");
        string[] lines =
        [
            "HƯỚNG DẪN ĐIỀN DANH SÁCH KHÁCH",
            "",
            "1. Mỗi dòng một khách. Cột SĐT (tô vàng) bắt buộc; các cột khác để trống được. Xóa 2 dòng mẫu trước khi nhập.",
            "2. SĐT: số Việt Nam 10 số, ví dụ 0912345678 (nhập +84... cũng được). Mỗi SĐT chỉ một dòng.",
            "3. Giới tính: Nam / Nữ / để trống. Nam → bot gọi \"anh\", Nữ → \"chị\", trống → \"mình\".",
            "4. Cách gọi (không bắt buộc): chỉ điền khi muốn gọi khác anh/chị: cô, chú, bác.",
            "5. Đã làm gì / Ngày làm: dịch vụ hoặc gói khách đã dùng (ghi đúng tên trong Kho kiến thức để bot hỏi thăm đúng gói). Ngày dạng 10/10/2026.",
            "6. Hẹn nhắn lúc: ngày giờ (giờ Việt Nam) cần chăm sóc lại, ví dụ 17/10/2026 09:00.",
            "   - Khách đã từng nhắn Zalo OA: tới giờ bot tự nhắn hỏi thăm.",
            "   - Khách chưa từng nhắn OA: tới giờ hiện ở trang Cần chăm sóc (mục \"Tới hẹn – chưa nhắn OA được\") để nhân viên gọi điện.",
            "7. Ghi chú cho AI: điều bot nên biết khi nhắn khách (sở thích, lưu ý...). Không ghi thông tin bệnh án nhạy cảm.",
            "8. Đồng ý nhận tin ưu đãi: Có / Không / để trống. Chỉ ghi \"Có\" khi khách đã đồng ý nhận tin khuyến mãi — doanh nghiệp chịu trách nhiệm về sự đồng ý này.",
            "9. Nhập lại cùng SĐT = cập nhật khách đó (không tạo trùng). Hệ thống hiện bản xem trước trước khi ghi.",
        ];
        for (var i = 0; i < lines.Length; i++)
        {
            guide.Cell(i + 1, 1).Value = lines[i];
        }

        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Column(1).Width = 120;
        workbook.SaveAs(output);
    }

    /// <summary>Đọc file; lỗi cả file (không mở được, thiếu cột SĐT, quá nhiều dòng) → <see cref="InvalidDataException"/> kèm câu tiếng Việt.</summary>
    public static IReadOnlyList<ContactImportRow> Read(Stream input, DateTimeOffset now)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(input);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("Không mở được file. Hãy dùng file mẫu .xlsx của hệ thống.", ex);
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(w => Normalize(w.Name) == Normalize(SheetName)) ?? workbook.Worksheets.First();
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (var c = 1; c <= lastColumn; c++)
            {
                columns.TryAdd(Normalize(sheet.Cell(1, c).GetString()), c);
            }

            if (!columns.ContainsKey(Normalize(PhoneHeader)))
            {
                throw new InvalidDataException($"Thiếu cột \"{PhoneHeader}\" ở dòng đầu. Không đổi tên tiêu đề cột của file mẫu.");
            }

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > MaxRows)
            {
                throw new InvalidDataException($"File có quá {MaxRows} khách. Chia thành nhiều file nhỏ hơn.");
            }

            string Text(int row, string header) =>
                columns.TryGetValue(Normalize(header), out var col) ? CellText(sheet.Cell(row, col)) : "";

            var rows = new List<ContactImportRow>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var r = 2; r <= lastRow; r++)
            {
                if (Headers.All(h => string.IsNullOrWhiteSpace(Text(r, h))))
                {
                    continue; // dòng trống
                }

                var errors = new List<string>();
                var rawPhone = Text(r, PhoneHeader);
                var phone = PhoneNumber.Normalize(rawPhone);
                if (phone is null)
                {
                    errors.Add(string.IsNullOrWhiteSpace(rawPhone) ? "Thiếu SĐT." : "SĐT không đúng dạng số Việt Nam 10 số.");
                }
                else if (seen.TryGetValue(phone, out var first))
                {
                    errors.Add($"SĐT trùng với dòng {first}.");
                }
                else
                {
                    seen[phone] = r;
                }

                var salutation = ParseSalutation(Text(r, SalutationHeader), Text(r, GenderHeader), errors);
                var serviceDate = ParseDate(sheet, columns, r, ServiceDateHeader, errors);
                var followUp = ParseDateTime(sheet, columns, r, FollowUpHeader, errors);
                var consent = ParseConsent(Text(r, ConsentHeader), errors);
                var name = Limit(Text(r, NameHeader), 200, NameHeader, errors);
                var service = Limit(Text(r, ServiceHeader), 2000, ServiceHeader, errors);
                var note = Limit(Text(r, NoteHeader), 2000, NoteHeader, errors);
                if (followUp is { } at && at < now.AddYears(-1))
                {
                    errors.Add("\"Hẹn nhắn lúc\" đã quá 1 năm.");
                }

                rows.Add(new ContactImportRow(r, phone, name, salutation, service, serviceDate, followUp, note, consent, errors));
            }

            return rows;
        }
    }

    private static string? ParseSalutation(string explicitValue, string gender, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            var value = explicitValue.Trim().ToLowerInvariant();
            if (CustomerSalutation.IsAllowed(value))
            {
                return value;
            }

            errors.Add("\"Cách gọi\" chỉ nhận: anh, chị, cô, chú, bác.");
            return null;
        }

        return Fold(gender) switch
        {
            "" => null,
            "nam" or "male" or "m" => "anh",
            "nu" or "female" or "f" => "chị",
            _ => AddError(errors, "\"Giới tính\" chỉ nhận: Nam, Nữ hoặc để trống."),
        };
    }

    private static bool? ParseConsent(string value, List<string> errors) => Fold(value) switch
    {
        "" => null,
        "co" or "x" or "yes" or "1" or "dong y" => true,
        "khong" or "no" or "0" or "tu choi" => false,
        _ => AddErrorBool(errors, "\"Đồng ý nhận tin ưu đãi\" chỉ nhận: Có, Không hoặc để trống."),
    };

    private static DateOnly? ParseDate(IXLWorksheet sheet, Dictionary<string, int> columns, int row, string header, List<string> errors)
    {
        if (!columns.TryGetValue(Normalize(header), out var col))
        {
            return null;
        }

        var cell = sheet.Cell(row, col);
        if (cell.DataType == XLDataType.DateTime)
        {
            return DateOnly.FromDateTime(cell.GetDateTime());
        }

        var text = CellText(cell);
        if (text.Length == 0)
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add($"\"{header}\" không đúng dạng ngày (ví dụ 10/10/2026).");
        return null;
    }

    private static DateTimeOffset? ParseDateTime(IXLWorksheet sheet, Dictionary<string, int> columns, int row, string header, List<string> errors)
    {
        if (!columns.TryGetValue(Normalize(header), out var col))
        {
            return null;
        }

        var cell = sheet.Cell(row, col);
        DateTime local;
        if (cell.DataType == XLDataType.DateTime)
        {
            local = cell.GetDateTime();
        }
        else
        {
            var text = CellText(cell);
            if (text.Length == 0)
            {
                return null;
            }

            if (!DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            {
                errors.Add($"\"{header}\" không đúng dạng ngày giờ (ví dụ 17/10/2026 09:00).");
                return null;
            }

            if (text.Length <= 10)
            {
                local = local.Date.AddHours(9); // chỉ có ngày → 9 giờ sáng
            }
        }

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Vietnam).ToUniversalTime();
    }

    private static string? Limit(string value, int max, string header, List<string> errors)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length > max)
        {
            errors.Add($"\"{header}\" dài quá {max} ký tự.");
            return null;
        }

        return value;
    }

    /// <summary>Bỏ dấu, chữ thường, gộp khoảng trắng, bỏ dấu * — để so tiêu đề cột và giá trị "Nữ"/"nu", "Có"/"co".</summary>
    private static string Fold(string? text)
    {
        var normalized = (text ?? "").Replace("*", "", StringComparison.Ordinal).Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var chars = normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static string CellText(IXLCell cell) => cell.GetFormattedString().Trim();

    private static string Normalize(string header) => Fold(header);

    private static string? AddError(List<string> errors, string message)
    {
        errors.Add(message);
        return null;
    }

    private static bool? AddErrorBool(List<string> errors, string message)
    {
        errors.Add(message);
        return null;
    }
}
