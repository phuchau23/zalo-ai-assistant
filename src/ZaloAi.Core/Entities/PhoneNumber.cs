namespace ZaloAi.Core.Entities;

/// <summary>Chuẩn hóa số điện thoại di động/cố định Việt Nam về dạng "0xxxxxxxxx" (10 số) để so khớp và băm.</summary>
public static class PhoneNumber
{
    /// <summary>"+84 912.345.678", "84912345678", "0912 345 678" → "0912345678". Không phải số VN 10 chữ số → null.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("84", StringComparison.Ordinal) && digits.Length == 11)
        {
            digits = "0" + digits[2..];
        }
        else if (digits.Length == 9 && digits[0] != '0')
        {
            digits = "0" + digits; // Excel hay làm mất số 0 đầu
        }

        return digits.Length == 10 && digits[0] == '0' && digits[1] != '0' ? digits : null;
    }

    /// <summary>"0912345678" → "0912***678" (hiện cho người không cần xem đủ số).</summary>
    public static string Mask(string normalized) =>
        normalized.Length == 10 ? $"{normalized[..4]}***{normalized[^3..]}" : "***";
}
