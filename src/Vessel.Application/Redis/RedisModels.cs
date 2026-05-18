namespace Vessel.Application.Redis;

public sealed record DistributedLockHandle(string Key, string OwnerToken, DateTimeOffset AcquiredAt, TimeSpan LeaseDuration)
    : IAsyncDisposable
{
    public Func<ValueTask>? ReleaseAsync { get; init; }

    public ValueTask DisposeAsync() => ReleaseAsync?.Invoke() ?? ValueTask.CompletedTask;
}
