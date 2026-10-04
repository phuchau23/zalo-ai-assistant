using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>
/// Zalo App + Official Account API (docs/zalo-api-notes.md). Secret chỉ đặt qua user-secrets/env, không trong appsettings.
/// Chưa cấu hình (AppId/AppSecret trống) → ứng dụng vẫn chạy, nhưng kết nối OA và webhook báo "chưa cấu hình Zalo".
/// </summary>
public sealed class ZaloOptions : IValidatableObject
{
    public const string SectionName = "Zalo";

    public string? AppId { get; set; }

    /// <summary>Khóa bí mật của app (header secret_key khi đổi code / làm mới token).</summary>
    public string? AppSecret { get; set; }

    /// <summary>Callback URL khai báo trong trang app (Official Account → Thiết lập chung), https công khai.</summary>
    [Url]
    public string? OAuthRedirectUrl { get; set; }

    /// <summary>"OA Secret Key" trên trang Webhook của app — dùng kiểm chữ ký X-ZEvent-Signature. KHÁC AppSecret.</summary>
    public string? WebhookSecret { get; set; }

    [Url]
    public string OAuthBaseUrl { get; set; } = "https://oauth.zaloapp.com/v4/oa/";

    [Url]
    public string OpenApiBaseUrl { get; set; } = "https://openapi.zalo.me/";

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Số tin tối đa hệ thống gửi qua một OA mỗi phút (giới hạn theo OA của Zalo tùy gói; app: 4.000 request/phút).</summary>
    [Range(1, 4000)]
    public int SendPerMinutePerOa { get; set; } = 120;

    /// <summary>Làm mới access token khi còn ít hơn số phút này (token sống 25 giờ).</summary>
    [Range(5, 1440)]
    public int RefreshBeforeExpiryMinutes { get; set; } = 120;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret) && !string.IsNullOrWhiteSpace(OAuthRedirectUrl);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(AppId) && !AppId.All(char.IsAsciiDigit))
        {
            yield return new ValidationResult("Zalo:AppId phải là dãy số (ID ứng dụng trên developers.zalo.me).", [nameof(AppId)]);
        }

        if (OAuthRedirectUrl is not null && !OAuthRedirectUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult("Zalo:OAuthRedirectUrl phải là địa chỉ https.", [nameof(OAuthRedirectUrl)]);
        }
    }
}
