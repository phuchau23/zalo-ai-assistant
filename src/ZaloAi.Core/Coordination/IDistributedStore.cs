namespace ZaloAi.Core.Coordination;

/// <summary>
/// Kho dùng chung giữa nhiều bản Api/Worker (Redis): khóa phân tán, đánh dấu đã xử lý, giá trị tạm có hạn, bộ đếm theo cửa sổ.
/// Key do bên gọi đặt, phải có tiền tố theo mục đích (ví dụ "zalo:lock:refresh:{oaId}"). Không lưu token hay nội dung tin ở đây.
/// </summary>
public interface IDistributedStore
{
    /// <summary>Giữ khóa tối đa <paramref name="ttl"/> (tự nhả nếu tiến trình chết). null = đang có người khác giữ.</summary>
    Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Ghi nếu chưa có (true = lần đầu). Dùng chống xử lý trùng.</summary>
    Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Lấy rồi xóa ngay (dùng một lần, ví dụ OAuth state). null = không có hoặc đã hết hạn.</summary>
    Task<string?> TakeAsync(string key, CancellationToken cancellationToken);

    /// <summary>Tăng bộ đếm; lần tăng đầu tiên đặt hạn <paramref name="window"/>. Trả giá trị sau khi tăng.</summary>
    Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken);
}
