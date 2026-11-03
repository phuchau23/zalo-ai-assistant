namespace ZaloAi.IndustryTemplates;

public sealed record IndustryInfo(string Slug, string Name);

/// <summary>
/// Danh sách ngành được chọn trong cài đặt tenant (docs/INDUSTRIES.md mục 4). Ngành "chưa mở" không có ở đây.
/// Nội dung mẫu ngành (persona, câu cấm...) thêm ở M3 qua skill add-industry-template.
/// </summary>
public static class IndustryCatalog
{
    public static IReadOnlyList<IndustryInfo> All { get; } =
    [
        new("spa", "Spa / thẩm mỹ"),
        new("nha-khoa", "Nha khoa"),
        new("bat-dong-san", "Bất động sản"),
        new("sua-nha", "Sửa chữa nhà"),
        new("ban-le", "Bán lẻ / shop"),
        new("giao-duc", "Giáo dục / trung tâm đào tạo"),
    ];

    public static bool IsKnown(string? slug) => All.Any(i => i.Slug == slug);
}
