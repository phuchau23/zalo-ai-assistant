namespace ZaloAi.Core.Channels;

/// <summary>
/// Nội dung lưu cho tin không phải chữ (GĐ1 chưa hiểu ảnh/âm thanh/file). Bộ não nhận ra các chuỗi này và trả lời cố định
/// (không gọi AI → không bịa nội dung ảnh): media → xin mô tả bằng chữ + chuyển nhân viên; sticker → đáp ngắn.
/// </summary>
public static class InboundText
{
    public const string Sticker = "[Khách gửi sticker]";

    private static readonly Dictionary<string, string> Media = new(StringComparer.Ordinal)
    {
        ["image"] = "[Khách gửi hình ảnh]",
        ["gif"] = "[Khách gửi ảnh động]",
        ["audio"] = "[Khách gửi tin nhắn thoại]",
        ["video"] = "[Khách gửi video]",
        ["file"] = "[Khách gửi tệp]",
        ["location"] = "[Khách gửi vị trí]",
        ["business_card"] = "[Khách gửi danh thiếp]",
    };

    /// <summary>Chuỗi lưu cho một loại đính kèm (image, video...), null nếu loại không cần thay (vd link: giữ nguyên chữ).</summary>
    public static string? ForAttachment(string type) =>
        type == "sticker" ? Sticker : Media.GetValueOrDefault(type);

    public static bool IsMedia(string text) => Media.ContainsValue(text);
}
