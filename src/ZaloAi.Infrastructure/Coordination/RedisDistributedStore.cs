using System.Security.Cryptography;
using StackExchange.Redis;
using ZaloAi.Core.Coordination;

namespace ZaloAi.Infrastructure.Coordination;

/// <summary>
/// <see cref="IDistributedStore"/> trên Redis. Mọi key có tiền tố "zaloai:" để dùng chung Redis với ứng dụng khác an toàn.
/// StackExchange.Redis không nhận CancellationToken: chỉ kiểm tra hủy trước khi gọi.
/// </summary>
public sealed class RedisDistributedStore(IConnectionMultiplexer redis) : IDistributedStore
{
    private const string Prefix = "zaloai:";

    /// <summary>Chỉ xóa khóa khi vẫn đúng là khóa của mình (tránh nhả nhầm khóa người khác vừa lấy sau khi khóa mình hết hạn).</summary>
    private const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        end
        return 0
        """;

    private IDatabase Db => redis.GetDatabase();

    public async Task<IAsyncDisposable?> TryAcquireLockAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var redisKey = new RedisKey(Prefix + key);
        var acquired = await Db.StringSetAsync(redisKey, token, ttl, When.NotExists);
        return acquired ? new Lock(Db, redisKey, token) : null;
    }

    public Task<bool> SetIfNotExistsAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Db.StringSetAsync(Prefix + key, value, ttl, When.NotExists);
    }

    public async Task<string?> TakeAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await Db.StringGetDeleteAsync(Prefix + key);
        return value.IsNull ? null : value.ToString();
    }

    public async Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var redisKey = new RedisKey(Prefix + key);
        var value = await Db.StringIncrementAsync(redisKey);
        if (value == 1)
        {
            await Db.KeyExpireAsync(redisKey, window);
        }

        return value;
    }

    private sealed class Lock(IDatabase db, RedisKey key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await db.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
    }
}
