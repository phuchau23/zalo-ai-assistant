using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Bắt buộc từ M4; hiện để trống được.</summary>
public sealed class ZaloOptions
{
    public const string SectionName = "Zalo";

    public string? AppId { get; set; }

    public string? AppSecret { get; set; }

    [Url]
    public string? OAuthRedirectUrl { get; set; }
}
