namespace ZaloAi.Core.Entities;

/// <summary>
/// Tài khoản đăng nhập admin. Không thuộc tenant nào: một người có thể là thành viên nhiều tenant qua <see cref="Membership"/>.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Luôn lưu dạng chữ thường, đã trim (xem <see cref="NormalizeEmail"/>).</summary>
    public required string Email { get; set; }

    public required string PasswordHash { get; set; }

    public required string Name { get; set; }

    /// <summary>Chỉ chủ dự án. Xem được mọi tenant ở trang super admin (M6).</summary>
    public bool IsSuperAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }
}
