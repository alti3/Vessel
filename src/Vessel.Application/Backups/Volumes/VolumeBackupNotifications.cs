using System.Text.Json;
using Vessel.Application.Notifications;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain.Notifications;
using Vessel.Domain.Storage;

namespace Vessel.Application.Backups.Volumes;

/// <summary>
///     Notifications of volume backups. They reuse the backup event types (and therefore their subscriptions, defaults
///     and throttling, DATA-08); the subject is the owning application or service.
/// </summary>
public static class VolumeBackupNotifications
{
    public static RaiseNotificationRequest Succeeded(StorageOwnerContext owner, Guid scheduleId, Guid executionId,
        string targetName)
    {
        return Build(NotificationEventTypes.BackupSucceeded, NotificationSeverity.Info, owner,
            $"Volume backup succeeded: {owner.Name}",
            $"Backup of storage '{targetName}' of '{owner.Name}' completed successfully.", scheduleId, executionId);
    }

    public static RaiseNotificationRequest Failed(StorageOwnerContext owner, Guid scheduleId, Guid executionId,
        string targetName)
    {
        return Build(NotificationEventTypes.BackupFailed, NotificationSeverity.Error, owner,
            $"Volume backup failed: {owner.Name}",
            $"Backup of storage '{targetName}' of '{owner.Name}' failed. Review the backup execution in Vessel for details.",
            scheduleId, executionId);
    }

    public static RaiseNotificationRequest Recovered(StorageOwnerContext owner, Guid scheduleId, string targetName)
    {
        return Build(NotificationEventTypes.BackupRecovered, NotificationSeverity.Info, owner,
            $"Volume backups work again: {owner.Name}",
            $"Backups of storage '{targetName}' of '{owner.Name}' succeeded again after failing.", scheduleId, null);
    }

    public static RaiseNotificationRequest Missing(StorageOwnerContext owner, Guid scheduleId, string targetName,
        int days)
    {
        return Build(NotificationEventTypes.BackupMissing, NotificationSeverity.Warning, owner,
            $"Volume backup missing: {owner.Name}",
            $"Storage '{targetName}' of '{owner.Name}' has not been backed up successfully in {days} day(s).",
            scheduleId, null);
    }

    public static RaiseNotificationRequest RecoveryNeedsAttention(StorageOwnerContext owner, Guid scheduleId,
        Guid executionId, string targetName)
    {
        return Build(NotificationEventTypes.BackupFailed, NotificationSeverity.Error, owner,
            $"Containers left stopped: {owner.Name}",
            $"Vessel could not start the containers that were stopped to back up or restore storage '{targetName}' " +
            $"of '{owner.Name}'. Start them on the server, then retry the recovery in Vessel.", scheduleId, executionId);
    }

    public static RaiseNotificationRequest RestoreFailed(StorageOwnerContext owner, Guid scheduleId,
        Guid restoreId, string targetName)
    {
        return Build(NotificationEventTypes.RestoreFailed, NotificationSeverity.Error, owner,
            $"Volume restore failed: {owner.Name}",
            $"Restoring storage '{targetName}' of '{owner.Name}' failed. Review the restore in Vessel for details.",
            scheduleId, restoreId);
    }

    private static RaiseNotificationRequest Build(string eventType, NotificationSeverity severity,
        StorageOwnerContext owner, string title, string message, Guid scheduleId, Guid? operationId)
    {
        var isApplication = owner.Kind == StorageOwnerKind.Application;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["volumeBackupScheduleId"] = scheduleId,
            ["operationId"] = operationId,
            ["ownerKind"] = owner.Kind.ToString(),
            ["ownerId"] = owner.Id
        });
        return new RaiseNotificationRequest(eventType, severity,
            isApplication ? NotificationTargetType.Application : NotificationTargetType.Team,
            owner.Id.ToString("D"), title, message, payload,
            isApplication ? $"/applications/{owner.Id:D}" : $"/services/{owner.Id:D}", null);
    }
}
