namespace ZaloAi.Core.Errors;

/// <summary>
/// Code cố đọc/ghi dữ liệu không thuộc tenant hiện tại. Đây là lỗi lập trình nghiêm trọng (không phải lỗi người dùng):
/// không bắt và bỏ qua, để nó nổi lên Sentry.
/// </summary>
public sealed class TenantIsolationException(string message) : Exception(message);
