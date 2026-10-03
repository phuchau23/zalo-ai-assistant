using Sentry;

namespace ZaloAi.Infrastructure.Logging;

/// <summary>
/// Lớp chặn cuối trước khi lỗi rời server sang Sentry (nước ngoài):
/// - message của exception và log: che SĐT VN, email (log đã qua <see cref="SensitiveDataRedactor"/>, exception thì chưa);
/// - bỏ query string (callback OAuth có "code"), cookie, header, body của request.
/// SendDefaultPii = false đã bỏ IP và thông tin user; phần này bổ sung cho dữ liệu nghiệp vụ.
/// </summary>
public static class SentryScrubber
{
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);

        if (sentryEvent.Message is { } message)
        {
            message.Message = Mask(message.Message);
            message.Formatted = Mask(message.Formatted);
        }

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = Mask(exception.Value);
        }

        var request = sentryEvent.Request;
        request.QueryString = null;
        request.Cookies = null;
        request.Data = null;
        request.Headers.Clear();

        return sentryEvent;
    }

    public static Breadcrumb? Scrub(Breadcrumb breadcrumb)
    {
        ArgumentNullException.ThrowIfNull(breadcrumb);

        // Breadcrumb là bất biến: tạo lại với message đã che. Bỏ data vì có thể chứa URL kèm query string.
        return new Breadcrumb(
            Mask(breadcrumb.Message) ?? "",
            breadcrumb.Type ?? "default",
            data: null,
            category: breadcrumb.Category,
            level: breadcrumb.Level);
    }

    private static string? Mask(string? text) => text is null ? null : SensitiveDataRedactor.MaskText(text);
}
