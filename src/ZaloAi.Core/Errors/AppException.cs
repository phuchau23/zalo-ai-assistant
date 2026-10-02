namespace ZaloAi.Core.Errors;

/// <summary>
/// Lỗi nghiệp vụ có chủ đích. API map sang mã HTTP tương ứng; message phải an toàn để trả cho client
/// (không chứa token, PII, chi tiết nội bộ).
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Mã lỗi ổn định cho FE, ví dụ "not_found".</summary>
    public string Code { get; }
}

/// <summary>404. Dùng cả khi dữ liệu thuộc tenant khác, để không lộ sự tồn tại.</summary>
public sealed class NotFoundException(string message = "Không tìm thấy.")
    : AppException("not_found", message);

/// <summary>403. Đã đăng nhập nhưng không đủ quyền trong tenant hiện tại.</summary>
public sealed class ForbiddenException(string message = "Không có quyền thực hiện.")
    : AppException("forbidden", message);

/// <summary>400. Input sai mà validator không bắt được (kiểm tra theo trạng thái).</summary>
public sealed class InvalidInputException(string message)
    : AppException("invalid_input", message);

/// <summary>409. Xung đột trạng thái, ví dụ email đã tồn tại.</summary>
public sealed class ConflictException(string message)
    : AppException("conflict", message);
