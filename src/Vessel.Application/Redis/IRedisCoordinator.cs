namespace Vessel.Application.Redis;

public interface IRedisCache
{
    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);

    Task SetStringAsync(string key, string value, TimeSpan? expiration, CancellationToken cancellationToken = default);

    Task<long> IncrementAsync(string key, TimeSpan? expiration, CancellationToken cancellationToken = default);
}

public interface IDistributedLockManager
{
    Task<DistributedLockHandle?> TryAcquireAsync(
        string key,
        TimeSpan leaseDuration,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default);
}
