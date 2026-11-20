using Vessel.Domain;

namespace Vessel.Application.Notifications;

/// <summary>
///     Raises team notification events from operational workflows (deployments, backups, server health,
///     certificates, proxy). Implementations hand the event to the background job pipeline
///     (<see cref="RaiseNotificationJob" /> then <see cref="DispatchNotificationJob" />) and never throw into the
///     producing workflow: any failure, including one thrown by a builder, is logged and swallowed, so a broken
///     notification channel can never fail or roll back a deployment, backup, or health poll.
/// </summary>
public interface INotificationEventPublisher
{
    Task PublishAsync(TeamId teamId, RaiseNotificationRequest notification,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Builds the notification lazily (for example from database lookups) and publishes it. Returning
    ///     <c>null</c> skips publication. Exceptions from <paramref name="build" /> are logged, never rethrown.
    /// </summary>
    Task PublishAsync(Func<TeamNotification?> build, CancellationToken cancellationToken = default);
}

public sealed record TeamNotification(TeamId TeamId, RaiseNotificationRequest Notification);
