using Vessel.Application.Notifications;
using Vessel.Domain;

namespace Vessel.UnitTests.Application;

internal sealed class RecordingNotificationEventPublisher : INotificationEventPublisher
{
    public List<TeamNotification> Published { get; } = [];

    public Task PublishAsync(TeamId teamId, RaiseNotificationRequest notification,
        CancellationToken cancellationToken = default)
    {
        Published.Add(new TeamNotification(teamId, notification));
        return Task.CompletedTask;
    }

    public Task PublishAsync(Func<TeamNotification?> build, CancellationToken cancellationToken = default)
    {
        var built = build();
        if (built is not null) Published.Add(built);
        return Task.CompletedTask;
    }
}
