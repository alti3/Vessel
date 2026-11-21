using System.Text.Json;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

/// <summary>
///     Event type names raised by Vessel producers. Every value is a key of the Domain
///     <see cref="NotificationEventCatalog" />, which defines per-target subscriptions, defaults, always-deliver events,
///     and throttling (NTF-04/NTF-05).
/// </summary>
public static class NotificationEventTypes
{
    public const string DeploymentSucceeded = NotificationEventCatalog.DeploymentSucceeded;
    public const string DeploymentFailed = NotificationEventCatalog.DeploymentFailed;
    public const string DeploymentCancelled = NotificationEventCatalog.DeploymentCancelled;
    public const string BackupSucceeded = NotificationEventCatalog.BackupSucceeded;
    public const string BackupFailed = NotificationEventCatalog.BackupFailed;
    public const string BackupMissing = NotificationEventCatalog.BackupMissing;
    public const string BackupRecovered = NotificationEventCatalog.BackupRecovered;
    public const string BackupDestinationUnusable = NotificationEventCatalog.BackupDestinationUnusable;
    public const string RestoreFailed = NotificationEventCatalog.RestoreFailed;
    public const string ServerUnreachable = NotificationEventCatalog.ServerUnreachable;
    public const string ServerReachable = NotificationEventCatalog.ServerReachable;
    public const string ServerDiskUsageHigh = NotificationEventCatalog.ServerDiskUsageHigh;
    public const string ServerSshHostKeyChanged = NotificationEventCatalog.ServerSshHostKeyChanged;
    public const string DockerCleanupSucceeded = NotificationEventCatalog.DockerCleanupSucceeded;
    public const string DockerCleanupFailed = NotificationEventCatalog.DockerCleanupFailed;
    public const string CertificateIssuanceFailed = NotificationEventCatalog.CertificateIssuanceFailed;
    public const string CertificateRenewalFailed = NotificationEventCatalog.CertificateRenewalFailed;
    public const string CertificateExpiringSoon = NotificationEventCatalog.CertificateExpiringSoon;
    public const string ProxyApplyFailed = NotificationEventCatalog.ProxyApplyFailed;
    public const string ProxyOutdated = NotificationEventCatalog.ProxyOutdated;
    public const string ApplicationStatusChanged = NotificationEventCatalog.ApplicationStatusChanged;
    public const string ApplicationRestartLimitReached = NotificationEventCatalog.ApplicationRestartLimitReached;
    public const string ScheduledTaskSucceeded = NotificationEventCatalog.ScheduledTaskSucceeded;
    public const string ScheduledTaskFailed = NotificationEventCatalog.ScheduledTaskFailed;
    public const string ScheduledTaskRecovered = NotificationEventCatalog.ScheduledTaskRecovered;
    public const string ScheduledJobMissed = NotificationEventCatalog.ScheduledJobMissed;
    public const string PlatformUpdateAvailable = NotificationEventCatalog.PlatformUpdateAvailable;
    public const string PlatformUpdateStarted = NotificationEventCatalog.PlatformUpdateStarted;
    public const string PlatformUpdateFailed = NotificationEventCatalog.PlatformUpdateFailed;
    public const string PlatformUpdateSucceeded = NotificationEventCatalog.PlatformUpdateSucceeded;
    public const string ControlPlaneBackupFailed = NotificationEventCatalog.ControlPlaneBackupFailed;
    public const string ApiTokenFailing = NotificationEventCatalog.ApiTokenFailing;

    /// <summary>A target was disabled after repeated configuration failures (always delivered, never by email).</summary>
    public const string ChannelDisabled = NotificationEventCatalog.ChannelDisabled;

    /// <summary>Operator-triggered test send; routed only to the target under test.</summary>
    public const string Test = NotificationEventCatalog.Test;
}

/// <summary>
///     Typed factories for operational notification events. Messages are deliberately generic: they carry
///     resource names and identifiers, never raw command output or exception text, so a notification cannot leak
///     secrets to an external channel.
/// </summary>
public static class OperationalNotifications
{
    public static RaiseNotificationRequest DeploymentFinished(Guid deploymentId, Guid applicationId,
        string applicationName, string? commitSha, DeploymentNotificationOutcome outcome)
    {
        var (eventType, severity, title, verb) = outcome switch
        {
            DeploymentNotificationOutcome.Succeeded => (NotificationEventTypes.DeploymentSucceeded,
                NotificationSeverity.Info, "Deployment succeeded", "succeeded"),
            DeploymentNotificationOutcome.Cancelled => (NotificationEventTypes.DeploymentCancelled,
                NotificationSeverity.Warning, "Deployment cancelled", "was cancelled"),
            _ => (NotificationEventTypes.DeploymentFailed, NotificationSeverity.Error, "Deployment failed",
                "failed")
        };

        var commit = string.IsNullOrWhiteSpace(commitSha) || commitSha == "pending"
            ? string.Empty
            : $" at commit {commitSha[..Math.Min(12, commitSha.Length)]}";
        return new RaiseNotificationRequest(eventType, severity, NotificationTargetType.Deployment,
            deploymentId.ToString("D"), $"{title}: {applicationName}",
            $"Deployment of application '{applicationName}'{commit} {verb}.",
            Payload(new Dictionary<string, object?>
            {
                ["deploymentId"] = deploymentId,
                ["applicationId"] = applicationId,
                ["outcome"] = outcome.ToString()
            }),
            $"/deployments/{deploymentId:D}", null);
    }

    public static RaiseNotificationRequest BackupSucceeded(Guid backupExecutionId, Guid databaseId,
        string databaseName)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.BackupSucceeded, NotificationSeverity.Info,
            NotificationTargetType.Database, databaseId.ToString("D"), $"Backup succeeded: {databaseName}",
            $"Backup of database '{databaseName}' completed successfully.",
            Payload(new Dictionary<string, object?>
            {
                ["backupExecutionId"] = backupExecutionId,
                ["databaseId"] = databaseId
            }),
            $"/databases/{databaseId:D}", null);
    }

    public static RaiseNotificationRequest BackupFailed(Guid backupExecutionId, Guid databaseId, string databaseName)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.BackupFailed, NotificationSeverity.Error,
            NotificationTargetType.Database, databaseId.ToString("D"), $"Backup failed: {databaseName}",
            $"Backup of database '{databaseName}' failed. Review the backup execution in Vessel for details.",
            Payload(new Dictionary<string, object?>
            {
                ["backupExecutionId"] = backupExecutionId,
                ["databaseId"] = databaseId
            }),
            $"/databases/{databaseId:D}", null);
    }

    public static RaiseNotificationRequest BackupRecovered(Guid scheduleId, Guid databaseId, string databaseName,
        string scheduleName)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.BackupRecovered, NotificationSeverity.Info,
            NotificationTargetType.Database, databaseId.ToString("D"), $"Backups work again: {databaseName}",
            $"Backup schedule '{scheduleName}' of database '{databaseName}' succeeded again after failing.",
            Payload(new Dictionary<string, object?>
            {
                ["backupScheduleId"] = scheduleId,
                ["databaseId"] = databaseId
            }),
            $"/databases/{databaseId:D}", null);
    }

    public static RaiseNotificationRequest BackupMissing(Guid scheduleId, Guid databaseId, string databaseName,
        string scheduleName, int days)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.BackupMissing, NotificationSeverity.Warning,
            NotificationTargetType.Database, databaseId.ToString("D"), $"Backup missing: {databaseName}",
            $"Backup schedule '{scheduleName}' of database '{databaseName}' has not produced a successful backup " +
            $"in {days} day(s).",
            Payload(new Dictionary<string, object?>
            {
                ["backupScheduleId"] = scheduleId,
                ["databaseId"] = databaseId,
                ["days"] = days
            }),
            $"/databases/{databaseId:D}", null);
    }

    public static RaiseNotificationRequest BackupDestinationUnusable(Guid destinationId, string destinationName)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.BackupDestinationUnusable,
            NotificationSeverity.Error, NotificationTargetType.Team, destinationId.ToString("D"),
            $"Backup destination unusable: {destinationName}",
            $"Backup destination '{destinationName}' failed its connection test. Backups cannot be copied to it " +
            "until it validates again; Vessel retries every hour.",
            Payload(new Dictionary<string, object?> { ["backupDestinationId"] = destinationId }),
            "/storages", null);
    }

    public static RaiseNotificationRequest RestoreFailed(Guid backupExecutionId, Guid targetDatabaseId,
        string databaseName)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.RestoreFailed, NotificationSeverity.Critical,
            NotificationTargetType.Database, targetDatabaseId.ToString("D"), $"Restore failed: {databaseName}",
            $"Restore into database '{databaseName}' failed. Review the backup execution in Vessel for details.",
            Payload(new Dictionary<string, object?>
            {
                ["backupExecutionId"] = backupExecutionId,
                ["databaseId"] = targetDatabaseId
            }),
            $"/databases/{targetDatabaseId:D}", null);
    }

    public static RaiseNotificationRequest ServerReachabilityChanged(Guid serverId, string serverName,
        bool reachable)
    {
        return reachable
            ? new RaiseNotificationRequest(NotificationEventTypes.ServerReachable, NotificationSeverity.Info,
                NotificationTargetType.Server, serverId.ToString("D"), $"Server reachable again: {serverName}",
                $"Server '{serverName}' is reachable again.",
                Payload(new Dictionary<string, object?> { ["serverId"] = serverId, ["reachable"] = true }),
                $"/servers/{serverId:D}", null)
            : new RaiseNotificationRequest(NotificationEventTypes.ServerUnreachable, NotificationSeverity.Critical,
                NotificationTargetType.Server, serverId.ToString("D"), $"Server unreachable: {serverName}",
                $"Server '{serverName}' is unreachable. Deployments and proxy changes targeting it will fail.",
                Payload(new Dictionary<string, object?> { ["serverId"] = serverId, ["reachable"] = false }),
                $"/servers/{serverId:D}", null);
    }

    /// <summary>
    ///     The SSH host key of a server changed. Carries only fingerprints (public data), so a changed key can be compared
    ///     out of band before an operator trusts it.
    /// </summary>
    public static RaiseNotificationRequest ServerHostKeyChanged(Guid serverId, string serverName,
        IReadOnlyList<string> presentedFingerprints, bool enforced)
    {
        var consequence = enforced
            ? "Vessel refuses to connect until an administrator trusts the new key."
            : "Vessel still connects (warn mode). Verify the new key and trust it, or investigate the server.";
        return new RaiseNotificationRequest(NotificationEventTypes.ServerSshHostKeyChanged, NotificationSeverity.Critical,
            NotificationTargetType.Server, serverId.ToString("D"), $"SSH host key changed: {serverName}",
            $"The SSH host key of server '{serverName}' changed. {consequence}",
            Payload(new Dictionary<string, object?>
            {
                ["serverId"] = serverId,
                ["fingerprints"] = presentedFingerprints,
                ["enforced"] = enforced
            }),
            $"/servers/{serverId:D}", null);
    }

    public static RaiseNotificationRequest CertificateFailed(Guid certificateId, Guid applicationId, string host,
        bool renewal)
    {
        var action = renewal ? "renewal" : "issuance";
        return new RaiseNotificationRequest(
            renewal ? NotificationEventTypes.CertificateRenewalFailed : NotificationEventTypes.CertificateIssuanceFailed,
            NotificationSeverity.Error, NotificationTargetType.Application, applicationId.ToString("D"),
            $"Certificate {action} failed: {host}",
            $"TLS certificate {action} for '{host}' failed. Review the certificate status in Vessel.",
            Payload(new Dictionary<string, object?>
            {
                ["certificateId"] = certificateId,
                ["applicationId"] = applicationId,
                ["host"] = host
            }),
            $"/applications/{applicationId:D}", null);
    }

    /// <summary>
    ///     A certificate in use on the server expires soon. Traefik renews certificates itself (about 30 days before
    ///     expiry), so this signals that renewal is not happening (DNS, port 80, rate limits or a stopped proxy).
    /// </summary>
    public static RaiseNotificationRequest CertificateExpiringSoon(Guid certificateId, Guid serverId,
        string serverName, string host, DateTimeOffset expiresAt)
    {
        var expires = expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'",
            System.Globalization.CultureInfo.InvariantCulture);
        return new RaiseNotificationRequest(NotificationEventTypes.CertificateExpiringSoon,
            NotificationSeverity.Warning, NotificationTargetType.Server, serverId.ToString("D"),
            $"Certificate expiring soon: {host}",
            $"The TLS certificate for '{host}' on server '{serverName}' expires at {expires} and has not been renewed. " +
            "Check that the domain resolves to the server and that the proxy is running.",
            Payload(new Dictionary<string, object?>
            {
                ["certificateId"] = certificateId,
                ["serverId"] = serverId,
                ["host"] = host,
                ["expiresAt"] = expiresAt
            }),
            $"/servers/{serverId:D}", null);
    }

    public static RaiseNotificationRequest ProxyApplyFailed(Guid serverId, string serverName, string stage)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.ProxyApplyFailed, NotificationSeverity.Error,
            NotificationTargetType.Server, serverId.ToString("D"), $"Proxy configuration failed: {serverName}",
            $"Reverse proxy configuration {stage} failed on server '{serverName}'. The previous configuration was kept.",
            Payload(new Dictionary<string, object?> { ["serverId"] = serverId, ["stage"] = stage }),
            $"/servers/{serverId:D}", null);
    }

    /// <summary>
    ///     Team notice that a channel was disabled after <see cref="NotificationTarget.AutoDisableThreshold" />
    ///     consecutive configuration failures. The reason is the safe, status-only failure reason.
    /// </summary>
    public static RaiseNotificationRequest ChannelDisabled(Guid notificationTargetId, string targetName,
        NotificationChannel channel, string reason)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.ChannelDisabled, NotificationSeverity.Error,
            NotificationTargetType.System, notificationTargetId.ToString("D"),
            $"{channel} notifications disabled: {targetName}",
            $"{channel} notification target '{targetName}' was disabled after {NotificationTarget.AutoDisableThreshold} " +
            $"failed deliveries in a row. Reason: {reason} Fix the settings and enable the target again.",
            Payload(new Dictionary<string, object?>
            {
                ["notificationTargetId"] = notificationTargetId,
                ["channel"] = channel.ToString()
            }),
            "/notifications", null);
    }

    public static RaiseNotificationRequest Test(Guid notificationTargetId, string targetName)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.Test, NotificationSeverity.Info,
            NotificationTargetType.System, notificationTargetId.ToString("D"), "Vessel test notification",
            $"This is a test notification for target '{targetName}'. If you can read this, delivery works.",
            "{}", null, null);
    }

    private static string Payload(Dictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values);
    }
}

public enum DeploymentNotificationOutcome
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2
}
