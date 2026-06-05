using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

public interface INotificationProvider
{
    NotificationChannel Channel { get; }

    Task<NotificationDeliveryResult> SendAsync(
        NotificationEventDispatchMessage notification,
        NotificationTargetDispatchContext target,
        CancellationToken cancellationToken = default);
}
