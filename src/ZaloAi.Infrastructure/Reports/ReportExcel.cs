using ClosedXML.Excel;

namespace ZaloAi.Infrastructure.Reports;

/// <summary>Xuất báo cáo doanh thu ra Excel (CRM C6): các sheet tổng hợp, theo kỳ, dịch vụ, nhân viên, chi nhánh, nguồn, thanh toán.</summary>
public static class ReportExcel
{
    public static byte[] Revenue(RevenueReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var workbook = new XLWorkbook();
        var summary = workbook.Worksheets.Add("Tổng hợp");
        summary.Cell(1, 1).Value = "Từ ngày";
        summary.Cell(1, 2).Value = report.From.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        summary.Cell(2, 1).Value = "Đến ngày";
        summary.Cell(2, 2).Value = report.To.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        summary.Cell(3, 1).Value = "Doanh thu";
        summary.Cell(3, 2).Value = report.Total;
        summary.Cell(4, 1).Value = "Hoàn tiền";
        summary.Cell(4, 2).Value = report.Refunds;
        summary.Cell(5, 1).Value = "Số hóa đơn";
        summary.Cell(5, 2).Value = report.Invoices;
        summary.Cell(6, 1).Value = "Kỳ trước";
        summary.Cell(6, 2).Value = report.PreviousTotal;
        summary.Column(1).AdjustToContents();

        var series = workbook.Worksheets.Add("Theo kỳ");
        Header(series, "Kỳ", "Doanh thu", "Số hóa đơn");
        var r = 2;
        foreach (var p in report.Series)
        {
            series.Cell(r, 1).Value = p.Period;
            series.Cell(r, 2).Value = p.Revenue;
            series.Cell(r, 3).Value = p.Invoices;
            r++;
        }

        Named(workbook.Worksheets.Add("Theo loại"), report.ByKind);
        Named(workbook.Worksheets.Add("Theo dịch vụ"), report.ByItem);
        Named(workbook.Worksheets.Add("Theo nhân viên"), report.ByStaff);
        Named(workbook.Worksheets.Add("Theo chi nhánh"), report.ByBranch);
        Named(workbook.Worksheets.Add("Theo nguồn khách"), report.BySource);
        Named(workbook.Worksheets.Add("Hình thức thanh toán"), report.ByPaymentMethod);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Named(IXLWorksheet sheet, IReadOnlyList<NamedAmount> rows)
    {
        Header(sheet, "Tên", "Doanh thu", "Số dòng");
        var r = 2;
        foreach (var row in rows)
        {
            sheet.Cell(r, 1).Value = row.Name;
            sheet.Cell(r, 2).Value = row.Amount;
            sheet.Cell(r, 3).Value = row.Count;
            r++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static void Header(IXLWorksheet sheet, params string[] titles)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = titles[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
        }
    }
}
