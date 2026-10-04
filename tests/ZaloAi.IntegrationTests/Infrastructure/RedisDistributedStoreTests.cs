using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ZaloAi.Core.Coordination;

namespace ZaloAi.IntegrationTests.Infrastructure;

[Collection(PostgresGroup.Name)]
public sealed class RedisDistributedStoreTests(PostgresFixture db)
{
    private IDistributedStore Store()
    {
        var services = db.BuildServices();
        return services.GetRequiredService<IDistributedStore>();
    }

    private static string Key(string name) => $"test:{name}:{Guid.NewGuid():N}";

    [Fact]
    public async Task Lock_is_exclusive_until_released()
    {
        var store = Store();
        var key = Key("lock");

        var first = await store.TryAcquireLockAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        first.ShouldNotBeNull();
        (await store.TryAcquireLockAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeNull();

        await first.DisposeAsync();
        var again = await store.TryAcquireLockAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        again.ShouldNotBeNull();
        await again.DisposeAsync();
    }

    [Fact]
    public async Task Expired_lock_owner_does_not_release_new_owner_lock()
    {
        var store = Store();
        var key = Key("lock-expire");

        var old = await store.TryAcquireLockAsync(key, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        old.ShouldNotBeNull();
        await Task.Delay(400);

        var fresh = await store.TryAcquireLockAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        fresh.ShouldNotBeNull();
        await old.DisposeAsync(); // khóa cũ đã hết hạn: không được xóa khóa mới

        (await store.TryAcquireLockAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None)).ShouldBeNull();
        await fresh.DisposeAsync();
    }

    [Fact]
    public async Task Set_if_not_exists_marks_first_time_only()
    {
        var store = Store();
        var key = Key("seen");

        (await store.SetIfNotExistsAsync(key, "1", TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBeTrue();
        (await store.SetIfNotExistsAsync(key, "1", TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Take_returns_value_once()
    {
        var store = Store();
        var key = Key("state");
        await store.SetIfNotExistsAsync(key, "payload", TimeSpan.FromMinutes(1), CancellationToken.None);

        (await store.TakeAsync(key, CancellationToken.None)).ShouldBe("payload");
        (await store.TakeAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Increment_counts_within_window()
    {
        var store = Store();
        var key = Key("rate");

        (await store.IncrementAsync(key, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBe(1);
        (await store.IncrementAsync(key, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBe(2);
    }
}
