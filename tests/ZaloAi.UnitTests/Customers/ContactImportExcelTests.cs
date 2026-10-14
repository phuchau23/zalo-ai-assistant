using ClosedXML.Excel;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Customers;

namespace ZaloAi.UnitTests.Customers;

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("0912345678", "0912345678")]
    [InlineData("0912.345.678", "0912345678")]
    [InlineData("+84 912 345 678", "0912345678")]
    [InlineData("84912345678", "0912345678")]
    [InlineData("912345678", "0912345678")] // Excel làm mất số 0 đầu
    [InlineData("02838123456", null)] // 11 số
    [InlineData("0012345678", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Normalizes_vietnamese_numbers(string raw, string? expected) => PhoneNumber.Normalize(raw).ShouldBe(expected);

    [Fact]
    public void Masks_the_middle() => PhoneNumber.Mask("0912345678").ShouldBe("0912***678");
}

public sealed class ContactImportExcelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 3, 0, 0, TimeSpan.Zero);

    private static MemoryStream Workbook(params string[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Khách hàng");
        string[] headers = ["SĐT", "Tên", "Giới tính", "Cách gọi", "Đã làm gì", "Ngày làm", "Hẹn nhắn lúc", "Ghi chú cho AI", "Đồng ý nhận tin ưu đãi"];
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];
            }
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Reads_rows_with_gender_salutation_dates_and_consent()
    {
        using var file = Workbook(
            ["0912 345 678", "Lan", "Nữ", "", "Gói bấm huyệt", "10/10/2026", "17/10/2026 09:00", "Hay đau vai", "Có"],
            ["0987654321", "Ông Ba", "Nam", "bác", "", "", "18/10/2026", "", "không"]);

        var rows = ContactImportExcel.Read(file, Now);

        var lan = rows[0];
        lan.Valid.ShouldBeTrue();
        lan.Phone.ShouldBe("0912345678");
        lan.Salutation.ShouldBe("chị");
        lan.ServiceDate.ShouldBe(new DateOnly(2026, 10, 10));
        lan.FollowUpAt.ShouldBe(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(7)));
        lan.Consent.ShouldBe(true);

        var ba = rows[1];
        ba.Salutation.ShouldBe("bác"); // "Cách gọi" thắng "Giới tính"
        ba.FollowUpAt.ShouldBe(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(7))); // chỉ ngày → 9 giờ sáng
        ba.Consent.ShouldBe(false);
    }

    [Fact]
    public void Reports_errors_per_row()
    {
        using var file = Workbook(
            ["12345", "A"],
            ["0912345678", "B", "Khác"],
            ["0912345678", "C"],
            ["0911111111", "D", "", "ông"],
            ["0922222222", "E", "", "", "", "32/13/2026"],
            ["", "", "", "", "", "", "", "", ""]);

        var rows = ContactImportExcel.Read(file, Now);

        rows.Count.ShouldBe(5); // dòng trống bị bỏ qua
        rows[0].Errors.ShouldContain(e => e.Contains("SĐT"));
        rows[1].Errors.ShouldContain(e => e.Contains("Giới tính"));
        rows[2].Errors.ShouldContain("SĐT trùng với dòng 3.");
        rows[3].Errors.ShouldContain(e => e.Contains("Cách gọi"));
        rows[4].Errors.ShouldContain(e => e.Contains("Ngày làm"));
    }

    [Fact]
    public void Template_samples_read_back_as_valid_rows()
    {
        using var buffer = new MemoryStream();
        ContactImportExcel.WriteTemplate(buffer);
        buffer.Position = 0;

        var rows = ContactImportExcel.Read(buffer, Now);

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Valid);
    }

    [Fact]
    public void File_without_phone_column_is_rejected()
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Khách hàng").Cell(1, 1).Value = "Tên";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        Should.Throw<InvalidDataException>(() => ContactImportExcel.Read(stream, Now)).Message.ShouldContain("SĐT");
    }
}
