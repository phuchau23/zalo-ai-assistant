using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Bot Telegram của hệ thống gửi thông báo cho nhân viên (mỗi DN điền chat id nhóm của họ). Token trống = tắt Telegram.</summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Token từ @BotFather — chỉ đặt qua user-secrets/env.</summary>
    public string? BotToken { get; set; }

    [Url]
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org/";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}
