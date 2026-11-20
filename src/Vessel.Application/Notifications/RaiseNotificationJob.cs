using Vessel.Domain;

namespace Vessel.Application.Notifications;

/// <summary>
///     Background entry point for producer-raised notifications. Persists the event, publishes the in-app
///     notification, and enqueues <see cref="DispatchNotificationJob" /> for external channels.
/// </summary>
public sealed class RaiseNotificationJob(NotificationEventService events)
{
    public async Task RunAsync(Guid teamId, RaiseNotificationRequest notification,
        CancellationToken cancellationToken = default)
    {
        await events.RaiseAsync(new TeamId(teamId), notification, cancellationToken);
    }
}
