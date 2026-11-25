namespace ZaloAi.Core.Channels;

/// <summary>
/// Nội dung lưu cho tin không phải chữ. Media (ảnh/âm thanh/file): bộ não trả lời cố định, không gọi AI (không bịa nội dung ảnh) —
/// xin mô tả bằng chữ + chuyển nhân viên. Sticker (2026-11-25): lưu "[Khách gửi sticker]", job xử lý tin đổi thành
/// "[Khách gửi sticker: &lt;nghĩa&gt;]" sau khi AI xem hình (StickerInterpreter) rồi bot trả lời theo ngữ cảnh như tin chữ.
/// </summary>
public static class InboundText
{
    public const string Sticker = "[Khách gửi sticker]";

    private const string StickerPrefix = "[Khách gửi sticker";

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

    /// <summary>Tin sticker, đã hiểu nghĩa hay chưa ("[Khách gửi sticker]" / "[Khách gửi sticker: …]").</summary>
    public static bool IsSticker(string? text) => text is not null && text.StartsWith(StickerPrefix, StringComparison.Ordinal);

    /// <summary>"[Khách gửi sticker: giơ ngón cái – đồng ý]" — nghĩa bỏ ngoặc vuông để không vỡ dấu hiệu.</summary>
    public static string StickerMeaning(string meaning)
    {
        ArgumentNullException.ThrowIfNull(meaning);
        return $"{StickerPrefix}: {meaning.Replace('[', '(').Replace(']', ')').Trim()}]";
    }
}
