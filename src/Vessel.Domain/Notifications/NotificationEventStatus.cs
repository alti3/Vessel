namespace Vessel.Domain.Notifications;

public enum NotificationEventStatus
{
    Pending = 0,
    Dispatching = 1,
    Delivered = 2,
    PartiallyFailed = 3,
    Failed = 4
}
