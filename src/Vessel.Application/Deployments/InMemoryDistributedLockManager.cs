using System.Collections.Concurrent;
using Vessel.Application.Redis;

namespace Vessel.Application.Deployments;

public sealed class InMemoryDistributedLockManager(TimeProvider timeProvider) : IDistributedLockManager
{
    private readonly ConcurrentDictionary<string, string> _locks = new(StringComparer.Ordinal);

    public async Task<DistributedLockHandle?> TryAcquireAsync(
        string key,
        TimeSpan leaseDuration,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
    {
        var owner = Guid.NewGuid().ToString("N");
        DateTimeOffset deadline = timeProvider.GetUtcNow().Add(waitTimeout);

        do
        {
            if (_locks.TryAdd(key, owner))
                return new DistributedLockHandle(key, owner, timeProvider.GetUtcNow(), leaseDuration)
                {
                    ReleaseAsync = () =>
                    {
                        _locks.TryRemove(new KeyValuePair<string, string>(key, owner));
                        return ValueTask.CompletedTask;
                    }
                };

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        } while (timeProvider.GetUtcNow() < deadline);

        return null;
    }
}
