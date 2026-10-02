using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

/// <summary>Đọc từ section chuẩn "ConnectionStrings" (env: ConnectionStrings__Postgres).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required]
    public string Postgres { get; set; } = "";

    /// <summary>Bắt buộc từ khi dùng Redis (bước 4).</summary>
    public string? Redis { get; set; }
}
