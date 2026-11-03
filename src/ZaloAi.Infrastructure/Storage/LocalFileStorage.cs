using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;
using ZaloAi.Core.Storage;

namespace ZaloAi.Infrastructure.Storage;

/// <summary>Lưu file vào thư mục trên máy. Chặn khóa trỏ ra ngoài thư mục gốc (path traversal).</summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.LocalRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zaloai", "files")
            : options.Value.LocalRoot);
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read));

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Khóa file không hợp lệ (trỏ ra ngoài thư mục lưu trữ).");
        }

        return path;
    }
}
