using System.Text.Json;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Notifications;

/// <summary>
///     Event type names raised by Vessel producers. The prefix (<c>deployment.</c>, <c>backup.</c>,
///     <c>server.</c>) selects the per-target event toggle in <see cref="NotificationDeliveryPolicy" />.
/// </summary>
public static class NotificationEventTypes
{
    public const string DeploymentSucceeded = "deployment.succeeded";
    public const string DeploymentFailed = "deployment.failed";
    public const string DeploymentCancelled = "deployment.cancelled";
    public const string BackupSucceeded = "backup.succeeded";
    public const string BackupFailed = "backup.failed";
    public const string RestoreFailed = "backup.restore_failed";
    public const string BackupRecovered = "backup.recovered";
    public const string BackupMissing = "backup.missing";
    public const string BackupDestinationUnusable = "backup.destination_unusable";
    public const string ServerUnreachable = "server.unreachable";
    public const string ServerReachable = "server.reachable";
    public const string CertificateIssuanceFailed = "certificate.issuance_failed";
    public const string CertificateRenewalFailed = "certificate.renewal_failed";
    public const string ProxyApplyFailed = "proxy.apply_failed";

    /// <summary>Operator-triggered test send; routed only to the target under test.</summary>
    public const string Test = "notification.test";
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

    public static RaiseNotificationRequest ProxyApplyFailed(Guid serverId, string serverName, string stage)
    {
        return new RaiseNotificationRequest(NotificationEventTypes.ProxyApplyFailed, NotificationSeverity.Error,
            NotificationTargetType.Server, serverId.ToString("D"), $"Proxy configuration failed: {serverName}",
            $"Reverse proxy configuration {stage} failed on server '{serverName}'. The previous configuration was kept.",
            Payload(new Dictionary<string, object?> { ["serverId"] = serverId, ["stage"] = stage }),
            $"/servers/{serverId:D}", null);
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
