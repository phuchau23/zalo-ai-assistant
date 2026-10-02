namespace ZaloAi.Core.Security;

/// <summary>Mã hóa trường nhạy cảm (token OA, SĐT, nội dung tin nhắn) trước khi lưu DB.</summary>
public interface IFieldEncryptor
{
    string Encrypt(string plaintext);

    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Sai định dạng, sai khóa, hoặc dữ liệu đã bị sửa.
    /// </exception>
    string Decrypt(string ciphertext);
}
