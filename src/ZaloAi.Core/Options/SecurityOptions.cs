using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

public sealed class SecurityOptions : IValidatableObject
{
    public const string SectionName = "Security";
    public const int EncryptionKeyBytes = 32;

    /// <summary>Khóa AES-256-GCM, 32 bytes mã hóa base64. Chỉ đặt qua env hoặc user-secrets.</summary>
    [Required]
    public string EncryptionKey { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrEmpty(EncryptionKey))
        {
            yield break; // [Required] đã báo lỗi.
        }

        // Không đưa giá trị khóa vào thông báo lỗi.
        var buffer = new byte[EncryptionKey.Length];
        if (!Convert.TryFromBase64String(EncryptionKey, buffer, out var length) || length != EncryptionKeyBytes)
        {
            yield return new ValidationResult(
                $"{nameof(EncryptionKey)} phải là {EncryptionKeyBytes} bytes mã hóa base64.",
                [nameof(EncryptionKey)]);
        }
    }
}
