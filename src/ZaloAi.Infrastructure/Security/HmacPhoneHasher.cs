using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Core.Security;

namespace ZaloAi.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 trên số điện thoại đã chuẩn hóa, khóa tách từ khóa mã hóa hệ thống bằng HKDF (mục đích riêng "phone-hash"), nên
/// không dùng chung khóa với AES. Đổi khóa mã hóa = phải băm lại toàn bộ SĐT (giống xoay khóa dữ liệu mã hóa).
/// </summary>
public sealed class HmacPhoneHasher : IPhoneHasher
{
    private readonly byte[] _key;

    public HmacPhoneHasher(IOptions<SecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var master = Convert.FromBase64String(options.Value.EncryptionKey);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: [], info: Encoding.UTF8.GetBytes("zaloai:phone-hash:v1"));
    }

    public string Hash(string normalizedPhone)
    {
        ArgumentNullException.ThrowIfNull(normalizedPhone);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalizedPhone)));
    }
}
