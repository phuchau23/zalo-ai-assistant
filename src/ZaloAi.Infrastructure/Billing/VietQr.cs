using System.Globalization;
using System.Text;
using QRCoder;

namespace ZaloAi.Infrastructure.Billing;

/// <summary>
/// Chuỗi VietQR (NAPAS 247, chuyển tới số tài khoản) theo EMVCo MPM — docs/payments-notes.md (kiểm tra 2026-10-15).
/// Không gọi dịch vụ ngoài; ảnh QR tạo tại chỗ bằng QRCoder.
/// </summary>
public static class VietQr
{
    private const string NapasGuid = "A000000727";
    private const string ServiceToAccount = "QRIBFTTA";

    /// <param name="amount">VNĐ; null hoặc 0 = mã tĩnh (khách tự nhập số tiền).</param>
    /// <param name="purpose">Nội dung chuyển khoản (được làm sạch: bỏ dấu, chỉ chữ số và khoảng trắng, ≤ 25 ký tự).</param>
    public static string Build(string bankBin, string accountNo, long? amount, string? purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankBin);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNo);

        var account = Field("00", bankBin.Trim()) + Field("01", accountNo.Trim());
        var merchant = Field("00", NapasGuid) + Field("01", account) + Field("02", ServiceToAccount);

        var s = new StringBuilder();
        s.Append(Field("00", "01"));
        s.Append(Field("01", amount is > 0 ? "12" : "11"));
        s.Append(Field("38", merchant));
        s.Append(Field("53", "704"));
        if (amount is > 0)
        {
            s.Append(Field("54", amount.Value.ToString(CultureInfo.InvariantCulture)));
        }

        s.Append(Field("58", "VN"));
        var clean = CleanPurpose(purpose);
        if (clean.Length > 0)
        {
            s.Append(Field("62", Field("08", clean)));
        }

        s.Append("6304");
        s.Append(Crc16(s.ToString()));
        return s.ToString();
    }

    /// <summary>PNG (byte) của mã QR, 8 px mỗi ô — đủ nét để quét từ màn hình điện thoại.</summary>
    public static byte[] Png(string payload, int pixelsPerModule = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    /// <summary>CRC16-CCITT (FALSE): poly 0x1021, init 0xFFFF, 4 ký tự hex in hoa.</summary>
    public static string Crc16(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc.ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>Bỏ dấu tiếng Việt, giữ chữ/số/khoảng trắng, gộp khoảng trắng, tối đa 25 ký tự.</summary>
    public static string CleanPurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return "";
        }

        var plain = new StringBuilder();
        foreach (var ch in purpose.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                plain.Append(char.ToUpperInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) && plain.Length > 0 && plain[^1] != ' ')
            {
                plain.Append(' ');
            }
        }

        var result = plain.ToString().Trim();
        return result.Length <= 25 ? result : result[..25].TrimEnd();
    }

    private static string Field(string id, string value) => id + value.Length.ToString("00", CultureInfo.InvariantCulture) + value;
}
