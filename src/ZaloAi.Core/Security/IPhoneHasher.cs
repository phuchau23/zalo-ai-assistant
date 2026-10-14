namespace ZaloAi.Core.Security;

/// <summary>
/// "Dấu vân tay" của số điện thoại (HMAC với khóa bí mật của hệ thống) để tìm/ghép khách theo SĐT mà không lưu SĐT dạng đọc được.
/// Cùng một số → cùng một chuỗi; không có khóa thì không dò ngược được.
/// </summary>
public interface IPhoneHasher
{
    /// <param name="normalizedPhone">Số đã chuẩn hóa bằng <see cref="Entities.PhoneNumber.Normalize"/>.</param>
    string Hash(string normalizedPhone);
}
