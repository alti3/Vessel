using Vessel.Domain;

namespace Vessel.Application.Notifications;

/// <summary>
///     Atomic, cross-worker duplicate suppression for notification events (NTF-04), keyed by team, event type, and
///     subject resource. Equivalent to upstream Coolify's <c>NotificationThrottle::claim/release</c>.
/// </summary>
public interface INotificationThrottle
{
    /// <summary>
    ///     Reserves the right to send. Returns <c>true</c> when nothing was sent for this key yet, or (with an
    ///     <paramref name="interval" />) when the last send is older than the interval. Without an interval the claim
    ///     holds until <see cref="ReleaseAsync" /> is called (once per incident).
    /// </summary>
    Task<bool> TryClaimAsync(TeamId teamId, string eventType, string subjectKey, TimeSpan? interval,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets a claim. Returns <c>true</c> when a claim existed (an incident was open).</summary>
    Task<bool> ReleaseAsync(TeamId teamId, string eventType, string subjectKey,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes claims older than <paramref name="olderThan" /> (housekeeping).</summary>
    Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}
