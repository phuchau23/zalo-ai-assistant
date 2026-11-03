using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using Shouldly;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using ZaloAi.Core.Errors;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Storage;

namespace ZaloAi.UnitTests.Knowledge;

public sealed class DocumentTextTests
{
    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Markdown_and_text_are_cleaned()
    {
        var text = DocumentText.Extract("ghi-chu.md", Utf8("# Bảng giá\r\n\r\n\r\nMassage   60 phút: 450.000đ\r\n\r\n\r\n\r\nGội đầu: 150.000đ"));

        text.ShouldBe("# Bảng giá\n\nMassage 60 phút: 450.000đ\n\nGội đầu: 150.000đ");
    }

    [Fact]
    public void Word_keeps_headings_and_tables()
    {
        using var buffer = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text("Dịch vụ massage"))),
                new Paragraph(new Run(new Text("Massage Đông y giúp thư giãn sâu, giảm căng cơ."))),
                new Table(
                    new TableRow(new TableCell(new Paragraph(new Run(new Text("Gói")))), new TableCell(new Paragraph(new Run(new Text("Giá"))))),
                    new TableRow(new TableCell(new Paragraph(new Run(new Text("60 phút")))), new TableCell(new Paragraph(new Run(new Text("450.000đ"))))))));
        }

        buffer.Position = 0;
        var text = DocumentText.Extract("gioi-thieu.docx", buffer);

        text.ShouldContain("# Dịch vụ massage");
        text.ShouldContain("Massage Đông y giúp thư giãn sâu");
        text.ShouldContain("60 phút | 450.000đ");
    }

    [Fact]
    public void Excel_rows_become_header_value_lines()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Bảng giá");
        sheet.Cell(1, 1).Value = "Dịch vụ";
        sheet.Cell(1, 2).Value = "Giá";
        sheet.Cell(2, 1).Value = "Gội đầu dưỡng sinh";
        sheet.Cell(2, 2).Value = "150.000đ";
        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;

        var text = DocumentText.Extract("bang-gia.xlsx", buffer);

        text.ShouldContain("# Bảng giá");
        text.ShouldContain("Dịch vụ: Gội đầu dưỡng sinh; Giá: 150.000đ");
    }

    [Fact]
    public void Pdf_text_is_extracted()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Massage 60 minutes costs 450000 VND at both branches.", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);

        var text = DocumentText.Extract("bang-gia.pdf", new MemoryStream(builder.Build()));

        text.ShouldContain("450000 VND");
    }

    [Fact]
    public void Broken_or_image_only_files_give_friendly_errors()
    {
        Should.Throw<DocumentReadException>(() => DocumentText.Extract("hong.pdf", Utf8("%PDF-1.7 rác")))
            .Message.ShouldContain("không đọc được");

        var empty = new PdfDocumentBuilder();
        empty.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        Should.Throw<DocumentReadException>(() => DocumentText.Extract("scan.pdf", new MemoryStream(empty.Build())))
            .Message.ShouldContain("ảnh scan");
    }

    [Fact]
    public void Chunker_starts_new_chunk_at_each_section_and_records_it()
    {
        var chunks = TextChunker.Split("Giới thiệu chung về phòng khám.\n\n# Bảng giá\n\nMassage 60 phút: 450.000đ\n\n# Chính sách\n\nHủy trước 2 giờ không mất phí.");

        chunks.Select(c => c.Section).ShouldBe([null, "Bảng giá", "Chính sách"]);
        chunks[1].Content.ShouldBe("Massage 60 phút: 450.000đ");
        chunks.Select(c => c.Ordinal).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void Chunker_splits_long_text_with_overlap_and_size_limit()
    {
        var paragraphs = Enumerable.Range(1, 40).Select(i => $"Đoạn số {i}: khách nên đặt lịch trước để được phục vụ tốt nhất tại chi nhánh gần nhà.");
        var chunks = TextChunker.Split(string.Join("\n\n", paragraphs));

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.Content.Length <= TextChunker.MaxChars + TextChunker.OverlapChars + 10);
        // Chồng lấn: đầu đoạn sau lặp lại phần cuối đoạn trước.
        var tailOfFirst = chunks[0].Content[^60..];
        chunks[1].Content.ShouldContain(tailOfFirst[^30..]);
        string.Join(" ", chunks.Select(c => c.Content)).ShouldContain("Đoạn số 40");
    }

    [Fact]
    public void Chunker_cuts_one_huge_paragraph()
    {
        var chunks = TextChunker.Split(new string('a', 5000));

        chunks.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [InlineData("..\\..\\Windows\\win.ini", "win.ini")]
    [InlineData("C:/fakepath/Bảng giá: 2026?.pdf", "Bảng giá 2026.pdf")]
    [InlineData("  tài liệu.docx  ", "tài liệu.docx")]
    public void Safe_file_name_strips_paths_and_forbidden_characters(string input, string expected)
    {
        KnowledgeDocumentService.SafeFileName(input).ShouldBe(expected);
    }

    [Fact]
    public void Safe_file_name_rejects_empty()
    {
        Should.Throw<InvalidInputException>(() => KnowledgeDocumentService.SafeFileName("../.."));
    }

    [Fact]
    public async Task Local_storage_round_trip_and_blocks_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "zaloai-unit", Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorage(Microsoft.Extensions.Options.Options.Create(new StorageOptions { LocalRoot = root }));

        await storage.SaveAsync("t1/d1/a.txt", Utf8("xin chào"), CancellationToken.None);
        await using (var read = await storage.OpenReadAsync("t1/d1/a.txt", CancellationToken.None))
        {
            new StreamReader(read).ReadToEnd().ShouldBe("xin chào");
        }

        await storage.DeleteAsync("t1/d1/a.txt", CancellationToken.None);
        File.Exists(Path.Combine(root, "t1", "d1", "a.txt")).ShouldBeFalse();
        await storage.DeleteAsync("t1/d1/a.txt", CancellationToken.None); // xóa lần 2 không lỗi

        await Should.ThrowAsync<InvalidOperationException>(() => storage.SaveAsync("../ngoai.txt", Utf8("x"), CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() => storage.OpenReadAsync("t1/../../ngoai.txt", CancellationToken.None));
    }
}
