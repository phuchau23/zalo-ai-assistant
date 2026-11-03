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

/// <summary>401. Sai email hoặc mật khẩu. Message chung, không cho biết email có tồn tại hay không.</summary>
public sealed class InvalidCredentialsException()
    : AppException("invalid_credentials", "Email hoặc mật khẩu không đúng.");

/// <summary>403. Đã đăng nhập nhưng không đủ quyền trong tenant hiện tại.</summary>
public sealed class ForbiddenException(string message = "Không có quyền thực hiện.", string code = "forbidden")
    : AppException(code, message)
{
    /// <summary>Chưa chọn tenant, hoặc tenant đã bị khóa / user không còn là thành viên.</summary>
    public static ForbiddenException NoActiveTenant() =>
        new("Tài khoản chưa thuộc doanh nghiệp nào đang hoạt động.", "no_active_tenant");
}

/// <summary>400. Input sai mà validator không bắt được (kiểm tra theo trạng thái).</summary>
public sealed class InvalidInputException(string message)
    : AppException("invalid_input", message);

/// <summary>409. Xung đột trạng thái, ví dụ email đã tồn tại.</summary>
public sealed class ConflictException(string message, string code = "conflict")
    : AppException(code, message);
