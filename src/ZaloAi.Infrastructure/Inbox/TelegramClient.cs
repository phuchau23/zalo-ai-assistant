using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;

namespace ZaloAi.Infrastructure.Inbox;

/// <summary>
/// Gửi thông báo cho nhóm nhân viên qua Telegram Bot API (sendMessage). Chỉ gửi loại sự việc + link hộp thư,
/// KHÔNG gửi nội dung tin hay thông tin cá nhân của khách (Telegram là bên thứ ba). Lỗi chỉ ghi log, không làm hỏng luồng chính.
/// </summary>
public sealed partial class TelegramClient(HttpClient http, IOptions<TelegramOptions> options, ILogger<TelegramClient> logger)
{
    public async Task<bool> SendAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var telegram = options.Value;
        if (!telegram.IsConfigured || string.IsNullOrWhiteSpace(chatId))
        {
            return false;
        }

        try
        {
            // Token nằm trong đường dẫn theo đặc tả Bot API — HttpClient logging đã hạ xuống Warning nên URL không bị ghi log.
            using var response = await http.PostAsJsonAsync(SendMessageUri(telegram), new { chat_id = chatId, text, disable_web_page_preview = true }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, (int)response.StatusCode, "http");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Thông báo là "có thì tốt": không làm hỏng job. Chỉ ghi LOẠI lỗi — message có thể chứa URL kèm token.
            LogFailed(logger, 0, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Ghép bằng chuỗi, KHÔNG dùng new Uri(base, relative): token có dấu ":" (vd "123:AAF...") khiến "bot123:..." bị hiểu là
    /// URI tuyệt đối với scheme "bot123" → HttpClient từ chối.
    /// </summary>
    public static Uri SendMessageUri(TelegramOptions telegram)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        return new Uri($"{telegram.ApiBaseUrl.TrimEnd('/')}/bot{telegram.BotToken}/sendMessage");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gửi Telegram thất bại (HTTP {Status}, {ErrorType})")]
    private static partial void LogFailed(ILogger logger, int status, string errorType);
}
