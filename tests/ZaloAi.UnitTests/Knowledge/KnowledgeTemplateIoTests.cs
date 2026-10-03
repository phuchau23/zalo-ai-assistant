using System.Text;
using ClosedXML.Excel;
using Shouldly;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Knowledge;

namespace ZaloAi.UnitTests.Knowledge;

public sealed class KnowledgeTemplateIoTests
{
    private static KnowledgeParseResult ReadJson(string json) =>
        KnowledgeJsonReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const string ValidJson = """
        {
          "format": "zaloai-knowledge",
          "version": 1,
          "info": [ { "code": "tt-hotline", "title": "Hotline", "content": "0909 000 000" } ],
          "services": [
            { "code": "DV-MASSAGE-60", "group": "Massage", "name": "Massage Đông y 60 phút", "durationMinutes": 60, "price": 450000, "priceUnit": "lượt" },
            { "code": "DV-KHOA-HOC", "name": "Khóa học", "price": null, "priceNote": "Liên hệ" },
          ],
          "packages": [ { "code": "GOI-10", "name": "Gói 10 buổi", "sessions": 10, "minutesPerSession": 90, "price": "13.000.000đ" } ],
          "faqs": [ { "Code": "FAQ-DAU", "Question": "Có đau không?", "Answer": "Không đau." } ],
          "policies": []
        }
        """;

    [Fact]
    public void Json_valid_file_is_read_and_normalized()
    {
        var result = ReadJson(ValidJson);

        result.Errors.ShouldBeEmpty();
        result.Entries.Count.ShouldBe(5);

        var info = result.Entries.Single(e => e.Kind == KnowledgeKind.Info);
        info.Code.ShouldBe("TT-HOTLINE"); // tự viết hoa

        var massage = result.Entries.Single(e => e.Code == "DV-MASSAGE-60");
        massage.Number("price").ShouldBe(450000);
        massage.Number("durationMinutes").ShouldBe(60);
        massage.Title.ShouldBe("Massage Đông y 60 phút");

        result.Entries.Single(e => e.Code == "DV-KHOA-HOC").Number("price").ShouldBeNull();
        result.Entries.Single(e => e.Code == "GOI-10").Number("price").ShouldBe(13_000_000); // "13.000.000đ"
        result.Entries.Single(e => e.Code == "FAQ-DAU").Text("answer").ShouldBe("Không đau."); // tên trường viết hoa vẫn đọc được
    }

    [Theory]
    [InlineData("""{ "services": [ { "code": "dv massage", "name": "X" } ] }""", "Mã", "chữ in hoa")]
    [InlineData("""{ "services": [ { "code": "DV-A", "name": "" } ] }""", "Tên dịch vụ", "Bắt buộc")]
    [InlineData("""{ "services": [ { "code": "DV-A", "name": "X", "price": "liên hệ" } ] }""", "Giá (VNĐ)", "Ghi chú giá")]
    [InlineData("""{ "services": [ { "code": "DV-A", "name": "X", "price": -5 } ] }""", "Giá (VNĐ)", "Ghi chú giá")]
    [InlineData("""{ "packages": [ { "code": "GOI-A", "name": "X", "sessions": 2.5 } ] }""", "Số buổi", "số nguyên")]
    [InlineData("""{ "faqs": [ { "code": "FAQ-A", "question": "Q", "answer": { "x": 1 } } ] }""", "Trả lời", "chữ hoặc số")]
    public void Json_invalid_values_report_column_and_reason(string json, string column, string reasonPart)
    {
        var result = ReadJson(json);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Column == column && e.Message.Contains(reasonPart, StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_code_across_kinds_is_reported_with_first_location()
    {
        var result = ReadJson("""
            { "services": [ { "code": "X-1", "name": "A" } ], "faqs": [ { "code": "x-1", "question": "Q", "answer": "A" } ] }
            """);

        var error = result.Errors.ShouldHaveSingleItem();
        error.Location.ShouldBe("Câu hỏi thường gặp");
        error.Message.ShouldContain("Dịch vụ, dòng 1");
    }

    [Theory]
    [InlineData("không phải json", "cú pháp")]
    [InlineData("[1,2]", "object")]
    [InlineData("""{ "format": "khac" }""", "format")]
    [InlineData("""{ "version": 2 }""", "phiên bản 1")]
    public void Json_file_level_errors(string json, string messagePart)
    {
        var error = ReadJson(json).Errors.ShouldHaveSingleItem();
        error.Row.ShouldBeNull();
        error.Message.ShouldContain(messagePart);
    }

    [Fact]
    public void Too_long_text_is_rejected()
    {
        var longName = new string('a', 201);
        var result = ReadJson($$"""{ "services": [ { "code": "DV-A", "name": "{{longName}}" } ] }""");

        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("200");
    }

    [Fact]
    public void Blank_excel_template_has_all_sheets_and_reads_as_empty()
    {
        using var buffer = new MemoryStream();
        KnowledgeExcel.Write([], buffer);

        buffer.Position = 0;
        using (var workbook = new XLWorkbook(buffer))
        {
            workbook.Worksheets.Select(w => w.Name).ShouldBe(
                ["Hướng dẫn", "Thông tin chung", "Dịch vụ", "Gói liệu trình", "Câu hỏi thường gặp", "Chính sách"]);
            workbook.Worksheet("Dịch vụ").Cell(1, 5).GetString().ShouldBe("Giá (VNĐ)");
        }

        buffer.Position = 0;
        var result = KnowledgeExcel.Read(buffer);
        result.Errors.ShouldBeEmpty();
        result.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Excel_round_trip_keeps_every_field()
    {
        var original = ReadJson(ValidJson).Entries;

        using var buffer = new MemoryStream();
        KnowledgeExcel.Write(original, buffer);
        buffer.Position = 0;
        var result = KnowledgeExcel.Read(buffer);

        result.Errors.ShouldBeEmpty();
        result.Entries.Count.ShouldBe(original.Count);
        foreach (var entry in original)
        {
            var back = result.Entries.Single(e => e.Code == entry.Code);
            KnowledgeItemMapper.ContentHash(back).ShouldBe(KnowledgeItemMapper.ContentHash(entry));
        }
    }

    [Fact]
    public void Excel_written_by_hand_is_read_leniently()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("dịch vụ"); // khác hoa thường
        sheet.Cell(1, 1).Value = "Mã *";
        sheet.Cell(1, 2).Value = "TÊN DỊCH VỤ";
        sheet.Cell(1, 3).Value = "Giá (VNĐ)";
        sheet.Cell(1, 4).Value = "Cột lạ";
        sheet.Cell(2, 1).Value = " dv-a ";
        sheet.Cell(2, 2).Value = "Massage";
        sheet.Cell(2, 3).Value = "450.000 đ";
        // dòng 3 để trống — bỏ qua
        sheet.Cell(4, 1).Value = "DV-B";
        sheet.Cell(4, 2).Value = "Gội đầu";
        sheet.Cell(4, 3).Value = 150000;

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;
        var result = KnowledgeExcel.Read(buffer);

        result.Errors.ShouldBeEmpty();
        result.Entries.Select(e => (e.Code, e.Number("price"))).ShouldBe([("DV-A", 450000L), ("DV-B", 150000L)]);
    }

    [Fact]
    public void Excel_missing_required_column_and_bad_row_are_located()
    {
        using var workbook = new XLWorkbook();
        var faq = workbook.Worksheets.Add("Câu hỏi thường gặp");
        faq.Cell(1, 1).Value = "Mã";
        faq.Cell(1, 2).Value = "Câu hỏi"; // thiếu cột Trả lời
        var services = workbook.Worksheets.Add("Dịch vụ");
        services.Cell(1, 1).Value = "Mã";
        services.Cell(1, 2).Value = "Tên dịch vụ";
        services.Cell(1, 3).Value = "Giá (VNĐ)";
        services.Cell(5, 1).Value = "DV-A";
        services.Cell(5, 2).Value = "X";
        services.Cell(5, 3).Value = "liên hệ";

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;
        var errors = KnowledgeExcel.Read(buffer).Errors;

        errors.ShouldContain(e => e.Location == "Câu hỏi thường gặp" && e.Row == 1 && e.Column == "Trả lời");
        errors.ShouldContain(e => e.Location == "Dịch vụ" && e.Row == 5 && e.Column == "Giá (VNĐ)");
    }

    [Fact]
    public void Not_an_excel_file_gives_friendly_error()
    {
        var result = KnowledgeExcel.Read(new MemoryStream(Encoding.UTF8.GetBytes("hello")));

        result.Errors.ShouldHaveSingleItem().Message.ShouldContain(".xlsx");
    }

    [Fact]
    public void Item_mapping_round_trip_and_search_text()
    {
        var entry = ReadJson(ValidJson).Entries.Single(e => e.Code == "DV-MASSAGE-60");

        var item = KnowledgeItemMapper.ToItem(entry, updatedBy: null);
        var back = KnowledgeItemMapper.FromItem(item);

        KnowledgeItemMapper.ContentHash(back).ShouldBe(item.ContentHash);
        item.ContentHash.Length.ShouldBe(64);
        item.DataJson.ShouldContain("Massage Đông y"); // không bị escape \u...
        item.SearchText.ShouldStartWith("[Dịch vụ] Massage Đông y 60 phút");
        item.SearchText.ShouldContain("Giá (VNĐ): 450.000 đ / lượt");
        item.SearchText.ShouldContain("Thời lượng (phút): 60");
    }

    [Fact]
    public void Json_export_can_be_read_back()
    {
        var original = ReadJson(ValidJson).Entries;

        using var buffer = new MemoryStream();
        KnowledgeItemMapper.WriteJsonFile(original, buffer);
        buffer.Position = 0;
        var result = KnowledgeJsonReader.Read(buffer);

        result.Errors.ShouldBeEmpty();
        result.Entries.Select(KnowledgeItemMapper.ContentHash).Order()
            .ShouldBe(original.Select(KnowledgeItemMapper.ContentHash).Order());
    }
}
