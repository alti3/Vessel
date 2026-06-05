namespace Vessel.Application.Notifications;

public sealed class DispatchNotificationJob(NotificationDispatchService dispatchService)
{
    public Task RunAsync(Guid notificationEventId, CancellationToken cancellationToken)
    {
        return dispatchService.DispatchAsync(notificationEventId, cancellationToken);
    }
}
