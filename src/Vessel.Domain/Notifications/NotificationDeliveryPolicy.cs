namespace Vessel.Domain.Notifications;

public readonly record struct NotificationDeliveryPolicy(
    NotificationSeverity MinimumSeverity,
    bool DeploymentEventsEnabled,
    bool BackupEventsEnabled,
    bool ServerEventsEnabled)
{
    public static NotificationDeliveryPolicy Default => new(NotificationSeverity.Info, true, true, true);
}
