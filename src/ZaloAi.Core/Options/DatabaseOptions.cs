using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Đọc từ section chuẩn "ConnectionStrings" (env: ConnectionStrings__Postgres).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required]
    public string Postgres { get; set; } = "";

    /// <summary>Redis: khóa khi làm mới token Zalo, OAuth state, chống trùng webhook, giới hạn tốc độ gửi (M4).</summary>
    [Required]
    public string Redis { get; set; } = "";
}
