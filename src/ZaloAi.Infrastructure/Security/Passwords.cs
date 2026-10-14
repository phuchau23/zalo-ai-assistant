using System.Security.Cryptography;
using System.Text;

namespace ZaloAi.Infrastructure.Security;

/// <summary>Mật khẩu tạm và "dấu vân tay" mật khẩu cho cookie phiên.</summary>
public static class Passwords
{
    public const int MinLength = 8;

    private const string TemporaryChars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Mật khẩu tạm 14 ký tự ngẫu nhiên (bỏ ký tự dễ nhầm 0/O, 1/l/I) — chỉ hiện một lần cho người cấp.</summary>
    public static string NewTemporary() =>
        string.Create(14, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = TemporaryChars[RandomNumberGenerator.GetInt32(TemporaryChars.Length)];
            }
        });

    /// <summary>
    /// Dấu vân tay của mật khẩu hiện tại (16 ký tự hex của SHA-256 trên chuỗi hash). Lưu trong cookie lúc đăng nhập; mỗi request so
    /// lại với DB → đổi/cấp lại mật khẩu là mọi phiên cũ bị đăng xuất. Không lộ được mật khẩu (băm của bản băm có salt).
    /// </summary>
    public static string Stamp(string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];
    }
}
