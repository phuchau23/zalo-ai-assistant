using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Security;

namespace ZaloAi.Infrastructure.Customers;

/// <summary>Đọc hồ sơ khách đã giải mã (chỉ dùng khi trả cho người có quyền xem).</summary>
public static class ContactView
{
    public static Dictionary<string, string> LeadFields(Contact contact, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(encryptor);
        return contact.LeadFieldsEnc is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(encryptor.Decrypt(contact.LeadFieldsEnc)) ?? [];
    }

    /// <summary>Tên hiển thị: tên kênh (Zalo) → tên khách tự khai với bot → "Khách …1234".</summary>
    public static string Name(Contact contact, IReadOnlyDictionary<string, string> leadFields)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(leadFields);
        if (!string.IsNullOrWhiteSpace(contact.DisplayName))
        {
            return contact.DisplayName;
        }

        if (leadFields.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var id = contact.ExternalUserId;
        return $"Khách …{(id.Length > 4 ? id[^4..] : id)}";
    }

    public static string StatusLabel(LeadStatus status) => status switch
    {
        LeadStatus.New => "Mới",
        LeadStatus.Interested => "Quan tâm",
        LeadStatus.Hot => "Nóng",
        LeadStatus.Won => "Đã chốt",
        LeadStatus.Lost => "Không tiềm năng",
        _ => status.ToString(),
    };
}

/// <summary>Một dòng xuất Excel danh sách khách.</summary>
public sealed record ContactExportRow(
    string Name,
    string Channel,
    LeadStatus Status,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> LeadFields,
    DateTimeOffset? LastCustomerMessageAt,
    DateTimeOffset CreatedAt);

public static class ContactExcel
{
    /// <param name="fieldLabels">Các thông tin thu thập theo mẫu ngành (khóa → nhãn cột), theo thứ tự cột.</param>
    public static void Write(IReadOnlyList<ContactExportRow> rows, IReadOnlyList<KeyValuePair<string, string>> fieldLabels, Stream output)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(fieldLabels);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Khách hàng");
        var headers = new List<string> { "Tên", "Kênh", "Trạng thái", "Nhãn" };
        headers.AddRange(fieldLabels.Select(f => f.Value));
        headers.AddRange(["Tin cuối của khách", "Ngày bắt đầu"]);
        for (var i = 0; i < headers.Count; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F0FE");
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var values = new List<string>
            {
                row.Name,
                row.Channel,
                ContactView.StatusLabel(row.Status),
                string.Join(", ", row.Tags),
            };
            values.AddRange(fieldLabels.Select(f => row.LeadFields.GetValueOrDefault(f.Key) ?? ""));
            values.Add(Format(row.LastCustomerMessageAt));
            values.Add(Format(row.CreatedAt));
            for (var c = 0; c < values.Count; c++)
            {
                // Luôn ghi dạng chữ (không phải công thức) — khách tự nhập "=..." cũng không chạy được trong Excel.
                var cell = sheet.Cell(r + 2, c + 1);
                cell.Style.NumberFormat.Format = "@";
                cell.Value = values[c];
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 200));
        workbook.SaveAs(output);
    }

    private static string Format(DateTimeOffset? value) =>
        value is { } v ? v.ToOffset(TimeSpan.FromHours(7)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "";
}

/// <summary>Một sheet Excel: tiêu đề cột + các dòng (đều là chữ).</summary>
public sealed record ExcelSheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Xuất toàn bộ dữ liệu của MỘT khách (quyền được biết/nhận dữ liệu cá nhân) thành file Excel nhiều sheet.</summary>
public static class ContactDataExcel
{
    public static void Write(IReadOnlyList<ExcelSheet> sheets, Stream output)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        using var workbook = new XLWorkbook();
        foreach (var data in sheets)
        {
            var sheet = workbook.Worksheets.Add(data.Name);
            for (var i = 0; i < data.Headers.Count; i++)
            {
                var cell = sheet.Cell(1, i + 1);
                cell.Value = data.Headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F0FE");
            }

            for (var r = 0; r < data.Rows.Count; r++)
            {
                for (var c = 0; c < data.Rows[r].Count; c++)
                {
                    // Luôn ghi dạng chữ (không phải công thức) — nội dung khách nhắn "=..." cũng không chạy được trong Excel.
                    var cell = sheet.Cell(r + 2, c + 1);
                    cell.Style.NumberFormat.Format = "@";
                    cell.Value = data.Rows[r][c];
                    cell.Style.Alignment.WrapText = true;
                }
            }

            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(1, Math.Min(data.Rows.Count + 1, 200));
            foreach (var column in sheet.ColumnsUsed().Where(col => col.Width > 80))
            {
                column.Width = 80;
            }
        }

        workbook.SaveAs(output);
    }

    public static string Format(DateTimeOffset? value) =>
        value is { } v ? v.ToOffset(TimeSpan.FromHours(7)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "";
}
