namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Tên hàng đợi Hangfire (chữ thường, không dấu gạch ngang). Thêm hàng đợi mới thì thêm vào <see cref="All"/>.
/// Thứ tự trong <see cref="All"/> = độ ưu tiên: tin nhắn khách xử lý trước việc nền (đánh chỉ mục, đọc tài liệu).
/// </summary>
public static class JobQueues
{
    public const string Messages = "messages";

    public const string Default = "default";

    public static readonly string[] All = [Messages, Default];
}
