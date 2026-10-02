using System.Security.Cryptography;
using Shouldly;
using ZaloAi.Core.Options;
using ZaloAi.Infrastructure.Security;

namespace ZaloAi.UnitTests.Security;

public sealed class AesGcmFieldEncryptorTests
{
    private static AesGcmFieldEncryptor CreateEncryptor(string? key = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            EncryptionKey = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

    [Theory]
    [InlineData("")]
    [InlineData("access-token-abc123")]
    [InlineData("Chị ơi em muốn đặt lịch bấm huyệt lúc 3 giờ chiều 🙏")]
    public void Encrypt_then_decrypt_returns_original(string plaintext)
    {
        var encryptor = CreateEncryptor();

        var cipher = encryptor.Encrypt(plaintext);

        encryptor.Decrypt(cipher).ShouldBe(plaintext);
    }

    [Fact]
    public void Encrypt_has_v1_format_and_does_not_contain_plaintext()
    {
        var cipher = CreateEncryptor().Encrypt("0912345678");

        cipher.ShouldStartWith("v1:");
        cipher.Split(':').Length.ShouldBe(4);
        cipher.ShouldNotContain("0912345678");
    }

    [Fact]
    public void Encrypting_same_text_twice_gives_different_ciphertext()
    {
        var encryptor = CreateEncryptor();

        encryptor.Encrypt("same").ShouldNotBe(encryptor.Encrypt("same"));
    }

    [Fact]
    public void Decrypt_with_wrong_key_throws()
    {
        var cipher = CreateEncryptor().Encrypt("secret");

        Should.Throw<CryptographicException>(() => CreateEncryptor().Decrypt(cipher));
    }

    [Fact]
    public void Decrypt_tampered_ciphertext_throws()
    {
        var encryptor = CreateEncryptor();
        var parts = encryptor.Encrypt("số tiền: 500000").Split(':');
        var bytes = Convert.FromBase64String(parts[3]);
        bytes[0] ^= 0x01;
        parts[3] = Convert.ToBase64String(bytes);

        Should.Throw<CryptographicException>(() => encryptor.Decrypt(string.Join(':', parts)));
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("v2:a:b:c")]
    [InlineData("v1:not-base64:!!:??")]
    [InlineData("v1:AAAA:AAAA:AAAA")]
    public void Decrypt_malformed_input_throws(string input)
    {
        Should.Throw<CryptographicException>(() => CreateEncryptor().Decrypt(input));
    }
}
