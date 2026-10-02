using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;

namespace ZaloAi.Infrastructure.Security;

/// <summary>
/// AES-256-GCM. Định dạng lưu: <c>v1:{nonce}:{tag}:{ciphertext}</c> (base64).
/// "v1" để sau này xoay khóa mà vẫn giải mã được dữ liệu cũ.
/// </summary>
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmFieldEncryptor(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _key = Convert.FromBase64String(options.Value.EncryptionKey);
        if (_key.Length != SecurityOptions.EncryptionKeyBytes)
        {
            throw new ArgumentException("Khóa mã hóa phải dài 32 bytes.", nameof(options));
        }
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];

        // AesGcm không đảm bảo thread-safe; tạo mới mỗi lần (rẻ).
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        return string.Join(':', Version, Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var parts = ciphertext.Split(':');
        if (parts.Length != 4 || parts[0] != Version)
        {
            throw new CryptographicException("Dữ liệu mã hóa sai định dạng hoặc phiên bản.");
        }

        byte[] nonce, tag, cipher;
        try
        {
            nonce = Convert.FromBase64String(parts[1]);
            tag = Convert.FromBase64String(parts[2]);
            cipher = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Dữ liệu mã hóa sai định dạng.", ex);
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new CryptographicException("Dữ liệu mã hóa sai định dạng.");
        }

        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            // Sai khóa hoặc dữ liệu bị sửa → AuthenticationTagMismatchException (kế thừa CryptographicException).
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return Encoding.UTF8.GetString(plain);
    }
}
