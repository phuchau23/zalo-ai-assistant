using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Api.Common;

public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimit";
    public const string LoginPolicy = "login";
    public const string ChatTestPolicy = "chat-test";
    public const string WebhookPolicy = "webhook";
    public const string OAuthCallbackPolicy = "oauth-callback";

    /// <summary>Số lần gọi /auth/login tối đa mỗi phút trên một IP (chống dò mật khẩu).</summary>
    [Range(1, 10_000)]
    public int LoginPermitPerMinute { get; set; } = 5;

    /// <summary>Số tin chat thử tối đa mỗi phút trên một người dùng (mỗi tin tốn tiền gọi AI).</summary>
    [Range(1, 10_000)]
    public int ChatTestPermitPerMinute { get; set; } = 20;

    /// <summary>Số request webhook tối đa mỗi phút trên một IP (Zalo gửi từ ít IP, nhiều sự kiện).</summary>
    [Range(1, 100_000)]
    public int WebhookPermitPerMinute { get; set; } = 3000;
}
