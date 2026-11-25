namespace ZaloAi.Core.Ai;

/// <summary>Kết quả AI xem hình sticker: một câu ngắn "hình gì – ý nghĩa" (vd "giơ ngón cái – đồng ý, ok") + chi phí lần gọi.</summary>
public sealed record StickerVisionResult(string Meaning, BotUsage Usage);

/// <summary>
/// AI xem hình sticker khách gửi để bot hiểu ý (chủ dự án chốt 2026-11-25: chữ khóa của Zalo + AI xem hình, lưu nhớ theo sticker).
/// Ảnh sticker là ảnh công khai của Zalo, không chứa dữ liệu cá nhân.
/// </summary>
public interface IStickerVision
{
    /// <param name="image">Ảnh sticker (png, jpeg, webp, gif).</param>
    /// <param name="keyword">Chữ khóa Zalo gắn với sticker (có thể trống / không chính xác) — chỉ để gợi ý.</param>
    /// <returns>null = AI không tả được. Lỗi gọi AI ném <see cref="Errors.AiProviderException"/>.</returns>
    Task<StickerVisionResult?> DescribeAsync(byte[] image, string mimeType, string? keyword, CancellationToken cancellationToken);
}
