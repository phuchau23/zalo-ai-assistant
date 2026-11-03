using Shouldly;
using ZaloAi.Infrastructure.Billing;

namespace ZaloAi.UnitTests.Billing;

/// <summary>VietQR theo EMVCo (docs/payments-notes.md): cấu trúc trường, CRC16-CCITT, làm sạch nội dung.</summary>
public sealed class VietQrTests
{
    [Fact]
    public void Crc16_ccitt_false_matches_reference_vector()
    {
        VietQr.Crc16("123456789").ShouldBe("29B1"); // vector chuẩn CRC-16/CCITT-FALSE
    }

    [Fact]
    public void Dynamic_payload_has_napas_fields_amount_purpose_and_valid_crc()
    {
        var payload = VietQr.Build("970436", "0123456789", 450000, "HD000012 Massage");

        payload.ShouldStartWith("000201"); // 00 = 01
        payload.ShouldContain("010212"); // 01 = 12 (động)
        payload.ShouldContain("0010A000000727"); // GUID NAPAS trong trường 38
        payload.ShouldContain("0006970436"); // BIN
        payload.ShouldContain("01100123456789"); // số tài khoản
        payload.ShouldContain("0208QRIBFTTA");
        payload.ShouldContain("5303704");
        payload.ShouldContain("5406450000");
        payload.ShouldContain("5802VN");
        payload.ShouldContain("0816HD000012 MASSAGE"); // nội dung đã làm sạch (in hoa, không dấu)
        payload[^8..^4].ShouldBe("6304");
        VietQr.Crc16(payload[..^4]).ShouldBe(payload[^4..]);
    }

    [Fact]
    public void Static_payload_without_amount_uses_11_and_no_54()
    {
        var payload = VietQr.Build("970436", "0123456789", null, null);
        payload.ShouldContain("010211");
        payload.ShouldNotContain("5406");
        payload.ShouldNotContain("6204");
    }

    [Theory]
    [InlineData("Thanh toán HĐ 12", "THANH TOAN HD 12")]
    [InlineData("  nhiều   khoảng   trắng  ", "NHIEU KHOANG TRANG")]
    [InlineData("Ký tự lạ #@! ok", "KY TU LA OK")]
    [InlineData("Dài quá hai mươi lăm ký tự để bị cắt bớt đi", "DAI QUA HAI MUOI LAM KY T")]
    public void Purpose_is_cleaned_to_safe_ascii(string raw, string expected) => VietQr.CleanPurpose(raw).ShouldBe(expected);

    [Fact]
    public void Png_is_generated()
    {
        var png = VietQr.Png(VietQr.Build("970436", "0123456789", 1000, "TEST"));
        png.Length.ShouldBeGreaterThan(100);
        png[..4].ShouldBe([0x89, 0x50, 0x4E, 0x47]); // chữ ký PNG
    }
}
