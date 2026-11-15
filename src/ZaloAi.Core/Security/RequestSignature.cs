using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ZaloAi.Core.Security;

/// <summary>
/// Chữ ký HMAC cho request nội bộ API ⇄ sidecar Zalo cá nhân (connectors/zalo-personal/CONTRACT.md mục 1):
/// <c>X-Remi-Signature: t=&lt;unix giây&gt;,v1=&lt;hex&gt;</c>, v1 = HMAC-SHA256(secret, "t.METHOD.pathAndQuery.body").
/// Ký cả thời điểm để request cũ bị chụp lại không dùng được quá 5 phút.
/// </summary>
public static class RequestSignature
{
    public const string HeaderName = "X-Remi-Signature";

    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static string Create(string secret, DateTimeOffset now, string method, string pathAndQuery, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var t = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Compute(secret, t, method, pathAndQuery, body)}";
    }

    public static bool IsValid(string? header, string secret, DateTimeOffset now, string method, string pathAndQuery, string body)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        string? t = null;
        string? v1 = null;
        foreach (var part in header.Split(','))
        {
            var kv = part.Trim();
            if (kv.StartsWith("t=", StringComparison.Ordinal))
            {
                t = kv[2..];
            }
            else if (kv.StartsWith("v1=", StringComparison.Ordinal))
            {
                v1 = kv[3..];
            }
        }

        if (t is null || v1 is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799));
        if ((now - sent).Duration() > MaxSkew)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, t, method, pathAndQuery, body));
        var actual = Encoding.ASCII.GetBytes(v1.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Compute(string secret, string t, string method, string pathAndQuery, string body)
    {
        var message = $"{t}.{method.ToUpperInvariant()}.{pathAndQuery}.{body}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }
}
