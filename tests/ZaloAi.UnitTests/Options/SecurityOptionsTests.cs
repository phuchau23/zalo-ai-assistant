using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Shouldly;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Options;

public sealed class SecurityOptionsTests
{
    private static List<ValidationResult> Validate(string key)
    {
        var options = new SecurityOptions { EncryptionKey = key };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Valid_32_byte_base64_key_passes()
    {
        Validate(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")] // 16 bytes
    public void Missing_or_invalid_key_fails(string key)
    {
        Validate(key).ShouldNotBeEmpty();
    }

    [Fact]
    public void Error_message_does_not_contain_key_value()
    {
        const string key = "c2hvcnQta2V5LXZhbHVl";

        Validate(key).ShouldAllBe(r => !r.ErrorMessage!.Contains(key));
    }
}
