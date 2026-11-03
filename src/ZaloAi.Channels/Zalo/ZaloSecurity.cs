using System.Security.Cryptography;
using System.Text;

namespace ZaloAi.Channels.Zalo;

/// <summary>PKCE cho OAuth v4 của Zalo (docs/zalo-api-notes.md mục 2).</summary>
public static class ZaloPkce
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Chuỗi 43 ký tự gồm chữ hoa, chữ thường, số — mỗi lần kết nối một chuỗi mới, giữ bí mật phía server.</summary>
    public static string NewVerifier() => RandomNumberGenerator.GetString(Alphabet, 43);

    /// <summary>code_challenge = Base64(SHA-256(ASCII(code_verifier))) không padding (đúng câu chữ docs).</summary>
    public static string Challenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=');
    }

    /// <summary>OAuth state ngẫu nhiên (khóa tra cứu verifier + tenant trong Redis).</summary>
    public static string NewState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

/// <summary>
/// Chữ ký webhook: header X-ZEvent-Signature, mac = sha256(appId + data + timeStamp + OAsecretKey), data = nguyên chuỗi body.
/// Chưa rõ header có tiền tố "mac=" không và timeStamp có đúng là trường "timestamp" trong body không (kiểm bằng nút Test của
/// Zalo) → chấp nhận cả có và không tiền tố, so sánh thời gian cố định, không phân biệt hoa thường của hex.
/// </summary>
public static class ZaloWebhookSignature
{
    public static string Compute(string appId, string rawBody, string timestamp, string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appId + rawBody + timestamp + secret))).ToLowerInvariant();

    public static bool IsValid(string? header, string appId, string rawBody, string timestamp, string secret)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var value = header.Trim();
        if (value.StartsWith("mac=", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..].Trim();
        }

        var expected = Encoding.ASCII.GetBytes(Compute(appId, rawBody, timestamp, secret));
        var actual = Encoding.ASCII.GetBytes(value.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Mô tả định dạng header (không lộ giá trị) để ghi log lần đầu khi xác minh với Zalo thật.</summary>
    public static string DescribeFormat(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return "missing";
        }

        var value = header.Trim();
        var prefix = value.StartsWith("mac=", StringComparison.OrdinalIgnoreCase) ? "mac=" : "";
        var body = value[prefix.Length..];
        var hex = body.All(Uri.IsHexDigit);
        var casing = body.Any(char.IsUpper) ? "upper" : "lower";
        return $"prefix='{prefix}' length={body.Length} hex={hex} case={casing}";
    }
}
