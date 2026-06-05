namespace Vessel.Domain.Notifications;

public enum NotificationDeliveryStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    RetryScheduled = 3
}
