using System.Text.RegularExpressions;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Knowledge;

public enum KnowledgeFieldType
{
    /// <summary>Mã mục: chữ in hoa không dấu, số, gạch ngang.</summary>
    Code,
    Text,

    /// <summary>Số nguyên dương (phút, số buổi).</summary>
    PositiveInt,

    /// <summary>Số nguyên VNĐ ≥ 0; chấp nhận "450.000đ" trong Excel.</summary>
    Money,
}

public sealed record KnowledgeField(string JsonName, string Header, KnowledgeFieldType Type, bool Required, int MaxLength = 0);

public sealed record KnowledgeKindDefinition(
    KnowledgeKind Kind,
    string SheetName,
    string JsonKey,
    string Label,
    IReadOnlyList<KnowledgeField> Fields)
{
    public KnowledgeField? FindByJsonName(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.JsonName, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Định nghĩa mẫu dữ liệu chuẩn, phiên bản 1 — nguồn duy nhất cho đọc file, tạo file mẫu, xuất dữ liệu, so sánh.
/// Đặc tả cho người đọc: docs/KNOWLEDGE-FORMAT.md (đổi ở đây thì cập nhật tài liệu đó).
/// </summary>
public static partial class KnowledgeTemplate
{
    public const string FormatName = "zaloai-knowledge";
    public const int FormatVersion = 1;
    public const string GuideSheetName = "Hướng dẫn";

    /// <summary>Giới hạn số mục trong một file, chặn file quá lớn làm chậm so sánh.</summary>
    public const int MaxEntries = 5000;

    private static readonly KnowledgeField Code = new("code", "Mã", KnowledgeFieldType.Code, Required: true, MaxLength: 50);

    public static IReadOnlyList<KnowledgeKindDefinition> Kinds { get; } =
    [
        new(KnowledgeKind.Info, "Thông tin chung", "info", "Thông tin chung",
        [
            Code,
            new("title", "Tiêu đề", KnowledgeFieldType.Text, true, 200),
            new("content", "Nội dung", KnowledgeFieldType.Text, true, 4000),
        ]),
        new(KnowledgeKind.Service, "Dịch vụ", "services", "Dịch vụ",
        [
            Code,
            new("group", "Nhóm", KnowledgeFieldType.Text, false, 100),
            new("name", "Tên dịch vụ", KnowledgeFieldType.Text, true, 200),
            new("durationMinutes", "Thời lượng (phút)", KnowledgeFieldType.PositiveInt, false),
            new("price", "Giá (VNĐ)", KnowledgeFieldType.Money, false),
            new("priceUnit", "Đơn vị giá", KnowledgeFieldType.Text, false, 50),
            new("priceNote", "Ghi chú giá", KnowledgeFieldType.Text, false, 500),
            new("description", "Mô tả", KnowledgeFieldType.Text, false, 4000),
            new("suitableFor", "Phù hợp với", KnowledgeFieldType.Text, false, 2000),
            new("notSuitableFor", "Không nên làm", KnowledgeFieldType.Text, false, 2000),
            new("branches", "Chi nhánh", KnowledgeFieldType.Text, false, 200),
            new("note", "Ghi chú", KnowledgeFieldType.Text, false, 2000),
        ]),
        new(KnowledgeKind.Package, "Gói liệu trình", "packages", "Gói liệu trình",
        [
            Code,
            new("group", "Nhóm", KnowledgeFieldType.Text, false, 100),
            new("name", "Tên gói", KnowledgeFieldType.Text, true, 200),
            new("sessions", "Số buổi", KnowledgeFieldType.PositiveInt, false),
            new("minutesPerSession", "Phút mỗi buổi", KnowledgeFieldType.PositiveInt, false),
            new("price", "Giá (VNĐ)", KnowledgeFieldType.Money, false),
            new("priceNote", "Ghi chú giá", KnowledgeFieldType.Text, false, 500),
            new("description", "Mô tả", KnowledgeFieldType.Text, false, 4000),
            new("suitableFor", "Phù hợp với", KnowledgeFieldType.Text, false, 2000),
            new("notSuitableFor", "Không nên làm", KnowledgeFieldType.Text, false, 2000),
            new("note", "Ghi chú", KnowledgeFieldType.Text, false, 2000),
        ]),
        new(KnowledgeKind.Faq, "Câu hỏi thường gặp", "faqs", "Câu hỏi thường gặp",
        [
            Code,
            new("question", "Câu hỏi", KnowledgeFieldType.Text, true, 500),
            new("answer", "Trả lời", KnowledgeFieldType.Text, true, 4000),
        ]),
        new(KnowledgeKind.Policy, "Chính sách", "policies", "Chính sách",
        [
            Code,
            new("topic", "Chủ đề", KnowledgeFieldType.Text, true, 200),
            new("content", "Nội dung", KnowledgeFieldType.Text, true, 4000),
        ]),
    ];

    public static KnowledgeKindDefinition For(KnowledgeKind kind) => Kinds.First(k => k.Kind == kind);

    /// <summary>Trường dùng làm "tên" để hiển thị và phát hiện mục có thể trùng.</summary>
    public static string TitleField(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Info => "title",
        KnowledgeKind.Faq => "question",
        KnowledgeKind.Policy => "topic",
        _ => "name",
    };

    /// <summary>Chuẩn hóa mã: bỏ khoảng trắng hai đầu, viết hoa. Trả null nếu sai quy tắc.</summary>
    public static string? NormalizeCode(string? raw)
    {
        var code = raw?.Trim().ToUpperInvariant();
        return code is not null && CodeRegex().IsMatch(code) ? code : null;
    }

    [GeneratedRegex("^[A-Z0-9-]{2,50}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CodeRegex();
}
