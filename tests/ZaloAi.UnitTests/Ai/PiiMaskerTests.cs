using Shouldly;
using ZaloAi.Ai.Privacy;

namespace ZaloAi.UnitTests.Ai;

public sealed class PiiMaskerTests
{
    [Theory]
    [InlineData("0912345678")]
    [InlineData("0912 345 678")]
    [InlineData("091.234.5678")]
    [InlineData("091-234-5678")]
    [InlineData("+84912345678")]
    [InlineData("84912345678")]
    [InlineData("0381234567")]
    [InlineData("02838123456")]
    public void Masks_vietnamese_phone_numbers(string phone)
    {
        var vault = new PiiVault();
        var masked = PiiMasker.Mask($"Số em là {phone} nha", vault);

        masked.ShouldBe("Số em là [PHONE_1] nha");
        PiiMasker.Restore("Bên em sẽ gọi [PHONE_1] ạ", vault).ShouldBe($"Bên em sẽ gọi {phone} ạ");
    }

    [Theory]
    [InlineData("Giá 450.000đ")]
    [InlineData("Gói 1.500.000 đồng")]
    [InlineData("Căn hộ 4500000000 VND")]
    [InlineData("Giá 450000000đ")]
    [InlineData("Mã DV-MASSAGE-60, 60 phút")]
    [InlineData("Ngày 12/10/2026 lúc 14:30")]
    [InlineData("1234567890")] // 10 số nhưng không phải đầu số di động
    public void Does_not_mask_prices_codes_and_dates(string text)
    {
        var vault = new PiiVault();
        PiiMasker.Mask(text, vault).ShouldBe(text);
        vault.Count.ShouldBe(0);
    }

    [Fact]
    public void Masks_email_and_cccd()
    {
        var vault = new PiiVault();
        var masked = PiiMasker.Mask("Email lan.nguyen@gmail.com, CCCD 079201001234", vault);

        masked.ShouldBe("Email [EMAIL_1], CCCD [ID_1]");
    }

    [Fact]
    public void Masks_nine_digit_cmnd_only_near_keyword()
    {
        var vault = new PiiVault();
        PiiMasker.Mask("số CMND của tôi là 123456789", vault).ShouldBe("số CMND của tôi là [ID_1]");
        PiiMasker.Mask("chuyển 123456789 đồng", vault).ShouldBe("chuyển 123456789 đồng");
    }

    [Fact]
    public void Same_value_gets_same_placeholder_across_messages()
    {
        var vault = new PiiVault();
        var first = PiiMasker.Mask("SĐT 0912345678", vault);
        var second = PiiMasker.Mask("gọi 0912345678 hoặc 0987654321", vault);

        first.ShouldBe("SĐT [PHONE_1]");
        second.ShouldBe("gọi [PHONE_1] hoặc [PHONE_2]");
    }

    [Fact]
    public void Masks_known_name_as_whole_word_case_insensitive()
    {
        var vault = new PiiVault();
        vault.AddKnown(PiiMasker.Name, "Nguyễn Thị Lan");

        PiiMasker.Mask("Chào chị nguyễn thị lan, chị Lan ơi", vault).ShouldBe("Chào chị [NAME_1], chị Lan ơi");
        PiiMasker.Restore("Dạ chào chị [NAME_1]", vault).ShouldBe("Dạ chào chị Nguyễn Thị Lan");
    }

    [Fact]
    public void Unknown_placeholders_are_removed_on_restore()
    {
        var vault = new PiiVault();
        PiiMasker.Restore("Gọi [PHONE_9] nhé", vault).ShouldBe("Gọi  nhé");
        PiiMasker.ContainsPlaceholder("Gọi [PHONE_9]").ShouldBeTrue();
    }
}
