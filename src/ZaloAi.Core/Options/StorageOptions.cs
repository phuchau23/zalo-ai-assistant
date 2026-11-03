namespace ZaloAi.Core.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Thư mục lưu file khi dùng kho local. Để trống = %LOCALAPPDATA%\zaloai\files (Api và Worker trên cùng máy dùng chung).
    /// Production nhiều máy chủ phải dùng object storage (chưa làm, chốt khi deploy).
    /// </summary>
    public string? LocalRoot { get; set; }
}
