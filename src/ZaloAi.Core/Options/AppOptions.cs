using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>URL trang quản trị (FE), dùng cho CORS và redirect sau OAuth.</summary>
    [Required, Url]
    public string AdminUrl { get; set; } = "";

    [Required, Url]
    public string ApiUrl { get; set; } = "";

    /// <summary>URL https công khai (ngrok/cloudflared) để Zalo gọi webhook. Bắt buộc từ M4.</summary>
    [Url]
    public string? PublicWebhookBaseUrl { get; set; }
}
