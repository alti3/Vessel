using System.Collections.Concurrent;

namespace Vessel.Application.Redis;

/// <summary>
///     Single-process stand-in for <see cref="IRedisCache" /> when Redis is disabled (development, single-node
///     installs). Counters and values expire like their Redis counterparts; nothing is shared across processes.
/// </summary>
public sealed class InMemoryRedisCache(TimeProvider timeProvider) : IRedisCache
{
    private const int SweepThreshold = 10_000;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TryGet(key, out Entry? entry) ? entry!.Value : null);
    }

    public Task SetStringAsync(string key, string value, TimeSpan? expiration,
        CancellationToken cancellationToken = default)
    {
        Sweep();
        _entries[key] = new Entry(value, Expiry(expiration));
        return Task.CompletedTask;
    }

    public Task<long> IncrementAsync(string key, TimeSpan? expiration, CancellationToken cancellationToken = default)
    {
        Sweep();
        DateTimeOffset now = timeProvider.GetUtcNow();
        Entry updated = _entries.AddOrUpdate(
            key,
            _ => new Entry("1", Expiry(expiration)),
            (_, existing) =>
            {
                var current = existing.ExpiresAt is { } expiresAt && expiresAt <= now
                    ? 0
                    : long.TryParse(existing.Value, out var parsed)
                        ? parsed
                        : 0;
                return new Entry((current + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Expiry(expiration) ?? existing.ExpiresAt);
            });
        return Task.FromResult(long.Parse(updated.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private bool TryGet(string key, out Entry? entry)
    {
        if (_entries.TryGetValue(key, out entry)
            && (entry.ExpiresAt is null || entry.ExpiresAt > timeProvider.GetUtcNow()))
            return true;

        entry = null;
        return false;
    }

    private DateTimeOffset? Expiry(TimeSpan? expiration)
    {
        return expiration is { } ttl ? timeProvider.GetUtcNow().Add(ttl) : null;
    }

    private void Sweep()
    {
        if (_entries.Count < SweepThreshold) return;
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (KeyValuePair<string, Entry> pair in _entries)
            if (pair.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
                _entries.TryRemove(pair);
    }

    private sealed record Entry(string Value, DateTimeOffset? ExpiresAt);
}
