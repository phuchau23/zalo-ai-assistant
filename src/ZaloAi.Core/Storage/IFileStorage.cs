namespace ZaloAi.Core.Storage;

/// <summary>
/// Kho lưu file gốc (tài liệu khách hàng nạp). Dev: thư mục trên máy; production: object storage (chốt khi deploy).
/// Khóa (key) do hệ thống tạo, dạng "{tenantId}/{documentId}/{tên-file-an-toàn}" — không bao giờ dùng nguyên tên file người dùng gửi.
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Xóa file; không có cũng không lỗi.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
