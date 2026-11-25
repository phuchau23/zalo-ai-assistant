using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Errors;

namespace ZaloAi.Infrastructure.Stickers;

/// <summary>Thông tin một sticker khách gửi: mã, chữ khóa Zalo gắn (có thể trống), đường dẫn ảnh (https).</summary>
public sealed record StickerInfo(string? Id, string? Keyword, string? ImageUrl);

/// <summary>Nghĩa của sticker + chi phí AI (null khi lấy từ bộ nhớ hoặc chỉ dùng chữ khóa).</summary>
public sealed record StickerMeaning(string Meaning, BotUsage? Usage);

/// <summary>
/// Hiểu sticker khách gửi (chủ dự án chốt 2026-11-25: chữ khóa của Zalo + AI xem hình, lưu nhớ để tiết kiệm). Mẫu Cache-Aside: tra Redis
/// theo mã sticker trước; chưa có mới tải ảnh (chỉ https, ≤ 1MB, đúng loại ảnh) cho AI tả rồi lưu 180 ngày — sticker hay dùng lặp lại nên
/// mỗi sticker gần như chỉ tốn một lần gọi. AI lỗi → dùng chữ khóa (lưu ngắn hạn để lần sau thử AI lại).
/// Lúc tin đến chưa gọi AI (webhook phải trả nhanh): kênh cất thông tin sticker theo mã tin (<see cref="RememberAsync"/>), job xử lý tin
/// mới hiểu nghĩa. Nghĩa sticker là dữ liệu chung (ảnh công khai của Zalo), không thuộc doanh nghiệp nào → khóa Redis không theo tenant.
/// </summary>
public sealed partial class StickerInterpreter(
    IDistributedStore store, IStickerVision vision, IHttpClientFactory httpFactory, ILogger<StickerInterpreter> logger)
{
    public const string ImageClient = "sticker-image";

    public const int MaxImageBytes = 1_000_000;

    private static readonly TimeSpan MetaTtl = TimeSpan.FromDays(2);

    private static readonly TimeSpan MeaningTtl = TimeSpan.FromDays(180);

    private static readonly TimeSpan KeywordOnlyTtl = TimeSpan.FromDays(1);

    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    public static string MetaKey(Guid messageId) => $"sticker:msg:{messageId:N}";

    /// <summary>Lúc tin đến: cất thông tin sticker theo mã tin nhắn để job xử lý tin đọc lại.</summary>
    public Task RememberAsync(Guid messageId, StickerInfo info, CancellationToken cancellationToken) =>
        store.SetAsync(MetaKey(messageId), JsonSerializer.Serialize(info), MetaTtl, cancellationToken);

    /// <summary>Nghĩa sticker của một tin. null = không có thông tin (tin cũ, kênh không gửi) hoặc không hiểu được.</summary>
    public async Task<StickerMeaning?> InterpretAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var raw = await store.GetAsync(MetaKey(messageId), cancellationToken);
        var info = raw is null ? null : JsonSerializer.Deserialize<StickerInfo>(raw);
        if (info is null)
        {
            return null;
        }

        var cacheKey = CacheKey(info);
        if (cacheKey is not null && await store.GetAsync(cacheKey, cancellationToken) is { Length: > 0 } cached)
        {
            return new StickerMeaning(cached, null);
        }

        if (SafeImageUri(info.ImageUrl) is { } uri && await DownloadAsync(uri, cancellationToken) is { } image)
        {
            try
            {
                if (await vision.DescribeAsync(image.Bytes, image.MimeType, info.Keyword, cancellationToken) is { } seen)
                {
                    if (cacheKey is not null)
                    {
                        await store.SetAsync(cacheKey, seen.Meaning, MeaningTtl, cancellationToken);
                    }

                    return new StickerMeaning(seen.Meaning, seen.Usage);
                }
            }
            catch (AiProviderException ex)
            {
                LogVisionFailed(logger, ex.GetType().Name);
            }
        }

        var keyword = info.Keyword?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return null;
        }

        var meaning = $"sticker “{(keyword.Length <= 40 ? keyword : keyword[..40])}”";
        if (cacheKey is not null)
        {
            await store.SetAsync(cacheKey, meaning, KeywordOnlyTtl, cancellationToken);
        }

        return new StickerMeaning(meaning, null);
    }

    /// <summary>Theo mã sticker; không có mã (vài kênh) thì theo băm đường dẫn ảnh.</summary>
    public static string? CacheKey(StickerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!string.IsNullOrWhiteSpace(info.Id))
        {
            return $"sticker:meaning:{info.Id.Trim()}";
        }

        return string.IsNullOrWhiteSpace(info.ImageUrl)
            ? null
            : $"sticker:meaning:u:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(info.ImageUrl.Trim())))[..32]}";
    }

    /// <summary>
    /// Chỉ tải ảnh qua https, tên miền (không phải địa chỉ IP / localhost) — đường dẫn đến từ Zalo (webhook có chữ ký, sidecar nội bộ)
    /// nhưng vẫn chặn kiểu dụ máy chủ gọi vào mạng nội bộ (SSRF).
    /// </summary>
    public static Uri? SafeImageUri(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback
            || uri.HostNameType is not UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        return uri;
    }

    private sealed record Image(byte[] Bytes, string MimeType);

    private async Task<Image?> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var http = httpFactory.CreateClient(ImageClient);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (!response.IsSuccessStatusCode || type is null || !ImageTypes.Contains(type)
                || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                LogImageRejected(logger, uri.Host, (int)response.StatusCode, type ?? "-");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes)
                {
                    LogImageRejected(logger, uri.Host, (int)response.StatusCode, "too_large");
                    return null;
                }
            }

            return buffer.Length == 0 ? null : new Image(buffer.ToArray(), type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Polly.Timeout.TimeoutRejectedException)
        {
            LogImageRejected(logger, uri.Host, 0, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không tải được ảnh sticker từ {Host} ({Status}, {Detail}) — dùng chữ khóa nếu có")]
    private static partial void LogImageRejected(ILogger logger, string host, int status, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI xem sticker lỗi ({ErrorType}) — dùng chữ khóa nếu có")]
    private static partial void LogVisionFailed(ILogger logger, string errorType);
}
