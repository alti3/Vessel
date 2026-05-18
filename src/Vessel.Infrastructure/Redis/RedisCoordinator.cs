using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Vessel.Application.Redis;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.Redis;

public sealed class RedisConnectionProvider(IOptionsMonitor<RedisOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _connection;

    public async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        RedisOptions redisOptions = options.CurrentValue;
        if (!redisOptions.Enabled || string.IsNullOrWhiteSpace(redisOptions.ConnectionString))
            throw new InvalidOperationException("Redis is not enabled.");

        if (_connection is { IsConnected: true }) return _connection.GetDatabase();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not { IsConnected: true })
                _connection = await ConnectionMultiplexer.ConnectAsync(redisOptions.ConnectionString);
        }
        finally
        {
            _gate.Release();
        }

        return _connection.GetDatabase();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}

public sealed class RedisCache(RedisConnectionProvider provider) : IRedisCache
{
    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        IDatabase db = await provider.GetDatabaseAsync(cancellationToken);
        return await db.StringGetAsync(key);
    }

    public async Task SetStringAsync(string key, string value, TimeSpan? expiration, CancellationToken cancellationToken = default)
    {
        IDatabase db = await provider.GetDatabaseAsync(cancellationToken);
        await db.StringSetAsync(key, value);
        if (expiration is not null) await db.KeyExpireAsync(key, expiration);
    }

    public async Task<long> IncrementAsync(string key, TimeSpan? expiration, CancellationToken cancellationToken = default)
    {
        IDatabase db = await provider.GetDatabaseAsync(cancellationToken);
        long value = await db.StringIncrementAsync(key);
        if (expiration is not null) await db.KeyExpireAsync(key, expiration);
        return value;
    }
}

public sealed class RedisDistributedLockManager(RedisConnectionProvider provider, TimeProvider timeProvider)
    : IDistributedLockManager
{
    public async Task<DistributedLockHandle?> TryAcquireAsync(
        string key,
        TimeSpan leaseDuration,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        string owner = Guid.NewGuid().ToString("N");
        DateTimeOffset deadline = timeProvider.GetUtcNow().Add(waitTimeout);
        IDatabase db = await provider.GetDatabaseAsync(cancellationToken);

        do
        {
            if (await db.LockTakeAsync(key, owner, leaseDuration))
            {
                return new DistributedLockHandle(key, owner, timeProvider.GetUtcNow(), leaseDuration)
                {
                    ReleaseAsync = async () => await db.LockReleaseAsync(key, owner)
                };
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        while (timeProvider.GetUtcNow() < deadline);

        return null;
    }
}
