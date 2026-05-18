namespace Vessel.Domain.Notifications;

public readonly record struct NotificationDeliveryPolicy(
    NotificationSeverity MinimumSeverity,
    bool DeploymentEventsEnabled,
    bool ServerEventsEnabled,
    bool SecurityEventsEnabled)
{
    public static NotificationDeliveryPolicy Default => new(NotificationSeverity.Info, true, true, true);
}
