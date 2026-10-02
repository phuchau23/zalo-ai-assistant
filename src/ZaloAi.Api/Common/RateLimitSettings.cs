using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Api.Common;

public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimit";
    public const string LoginPolicy = "login";

    /// <summary>Số lần gọi /auth/login tối đa mỗi phút trên một IP (chống dò mật khẩu).</summary>
    [Range(1, 10_000)]
    public int LoginPermitPerMinute { get; set; } = 5;
}
