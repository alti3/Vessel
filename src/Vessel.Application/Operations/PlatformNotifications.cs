using System.Text.Json;
using Vessel.Application.Authorization;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Operations;

/// <summary>Audit action names of control-plane operations (OPS-01/OPS-02/DATA-13).</summary>
public static class OperationsAuditActions
{
    public const string UpdatePolicyChanged = "instance.update_policy_changed";
    public const string UpdateChecked = "instance.update_checked";
    public const string BackupSettingsChanged = "instance.backup_settings_changed";
    public const string ControlPlaneBackupCompleted = "instance.backup_completed";
    public const string ControlPlaneBackupFailed = "instance.backup_failed";
    public const string ControlPlaneBackupDownloaded = "instance.backup_downloaded";
    public const string ControlPlaneBackupDeleted = "instance.backup_deleted";
    public const string UpgradeStarted = "instance.upgrade_started";
    public const string UpgradeReported = "instance.upgrade_reported";
    public const string UpgradeAborted = "instance.upgrade_aborted";
    public const string DrainStarted = "instance.drain_started";
    public const string DrainReleased = "instance.drain_released";
}

/// <summary>
///     Notification factories of platform lifecycle events. Messages carry versions and identifiers only, never command
///     output, paths or connection details.
/// </summary>
public static class PlatformNotifications
{
    public static RaiseNotificationRequest UpdateAvailable(string currentVersion, string latestVersion,
        string channel)
    {
        return Build(NotificationEventTypes.PlatformUpdateAvailable, NotificationSeverity.Info, $"update:{latestVersion}",
            $"Vessel {latestVersion} is available",
            $"Vessel {latestVersion} is available on the {channel} channel (installed: {currentVersion}).",
            new Dictionary<string, object?>
            {
                ["currentVersion"] = currentVersion,
                ["latestVersion"] = latestVersion,
                ["channel"] = channel
            });
    }

    public static RaiseNotificationRequest UpdateStarted(Guid upgradeRunId, string fromVersion, string targetVersion)
    {
        return Build(NotificationEventTypes.PlatformUpdateStarted, NotificationSeverity.Info,
            upgradeRunId.ToString("D"), $"Vessel update to {targetVersion} started",
            $"The update from {fromVersion} to {targetVersion} started.",
            new Dictionary<string, object?>
            {
                ["upgradeRunId"] = upgradeRunId,
                ["fromVersion"] = fromVersion,
                ["targetVersion"] = targetVersion
            });
    }

    public static RaiseNotificationRequest UpdateFinished(Guid upgradeRunId, string fromVersion, string targetVersion,
        bool succeeded, string status)
    {
        return Build(succeeded ? NotificationEventTypes.PlatformUpdateSucceeded : NotificationEventTypes.PlatformUpdateFailed,
            succeeded ? NotificationSeverity.Info : NotificationSeverity.Error, upgradeRunId.ToString("D"),
            succeeded ? $"Vessel updated to {targetVersion}" : $"Vessel update to {targetVersion} did not finish",
            succeeded
                ? $"Vessel was updated from {fromVersion} to {targetVersion}."
                : $"The update from {fromVersion} to {targetVersion} ended with status {status}. Review the upgrade status in Vessel.",
            new Dictionary<string, object?>
            {
                ["upgradeRunId"] = upgradeRunId,
                ["fromVersion"] = fromVersion,
                ["targetVersion"] = targetVersion,
                ["status"] = status
            });
    }

    public static RaiseNotificationRequest ControlPlaneBackupFailed(Guid backupId, string trigger)
    {
        return Build(NotificationEventTypes.ControlPlaneBackupFailed, NotificationSeverity.Error, "control-plane-backup",
            "Vessel database backup failed",
            $"A {trigger} backup of the Vessel database failed. Review the instance backups in Vessel for details.",
            new Dictionary<string, object?> { ["backupId"] = backupId, ["trigger"] = trigger });
    }

    private static RaiseNotificationRequest Build(string eventType, NotificationSeverity severity, string targetId,
        string title, string message, Dictionary<string, object?> payload)
    {
        return new RaiseNotificationRequest(eventType, severity, NotificationTargetType.System, targetId, title,
            message, JsonSerializer.Serialize(payload), "/settings/instance", null);
    }
}

/// <summary>Publishes platform notifications to every team whose Owner/Admin includes an instance administrator.</summary>
public sealed class PlatformNotificationPublisher(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    INotificationEventPublisher notifications)
{
    public async Task PublishAsync(RaiseNotificationRequest notification, CancellationToken cancellationToken = default)
    {
        TeamId[] teams = dbContext.Teams.Select(team => team.Id).ToArray();
        foreach (TeamId teamId in teams.Where(authorization.TeamHasInstanceAdmin))
            await notifications.PublishAsync(teamId, notification, cancellationToken);
    }
}
