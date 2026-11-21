using System.Globalization;
using System.Text.Json;
using Vessel.Application.Notifications;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Notifications;

namespace Vessel.Application.Monitoring;

/// <summary>
///     Notification factories of the monitoring producers (SRV-04, SRV-07, SRV-08). Like
///     <see cref="OperationalNotifications" />, messages carry names, identifiers and numbers only, never command output.
/// </summary>
public static class MonitoringNotifications
{
    public static RaiseNotificationRequest DiskUsageHigh(Guid serverId, string serverName, decimal usagePercent,
        int thresholdPercent, string? mountPoint)
    {
        var usage = Math.Round(usagePercent, 0).ToString(CultureInfo.InvariantCulture);
        var mount = string.IsNullOrWhiteSpace(mountPoint) ? string.Empty : $" ({mountPoint})";
        return new RaiseNotificationRequest(NotificationEventTypes.ServerDiskUsageHigh, NotificationSeverity.Warning,
            NotificationTargetType.Server, serverId.ToString("D"), $"High disk usage: {serverName}",
            $"Disk usage on server '{serverName}'{mount} is {usage}%, at or above the {thresholdPercent}% alert " +
            "threshold. Free space or run a Docker cleanup before deployments start failing.",
            Payload(new Dictionary<string, object?>
            {
                ["serverId"] = serverId,
                ["diskUsagePercent"] = usagePercent,
                ["thresholdPercent"] = thresholdPercent,
                ["mountPoint"] = mountPoint
            }),
            $"/servers/{serverId:D}", null);
    }

    public static RaiseNotificationRequest DockerCleanupFinished(Guid serverId, string serverName,
        Guid executionId, bool succeeded, long reclaimedBytes)
    {
        return succeeded
            ? new RaiseNotificationRequest(NotificationEventTypes.DockerCleanupSucceeded, NotificationSeverity.Info,
                NotificationTargetType.Server, serverId.ToString("D"), $"Docker cleanup finished: {serverName}",
                $"Docker cleanup on server '{serverName}' reclaimed {FormatBytes(reclaimedBytes)}.",
                Payload(new Dictionary<string, object?>
                {
                    ["serverId"] = serverId,
                    ["executionId"] = executionId,
                    ["reclaimedBytes"] = reclaimedBytes
                }),
                $"/servers/{serverId:D}", null)
            : new RaiseNotificationRequest(NotificationEventTypes.DockerCleanupFailed, NotificationSeverity.Error,
                NotificationTargetType.Server, serverId.ToString("D"), $"Docker cleanup failed: {serverName}",
                $"Docker cleanup on server '{serverName}' failed. Review the cleanup execution in Vessel for details.",
                Payload(new Dictionary<string, object?> { ["serverId"] = serverId, ["executionId"] = executionId }),
                $"/servers/{serverId:D}", null);
    }

    public static RaiseNotificationRequest RestartLimitReached(ResourceRuntimeKind kind, Guid resourceId,
        string resourceName, int restartCount, int maxRestartCount)
    {
        var label = KindLabel(kind);
        return new RaiseNotificationRequest(NotificationEventTypes.ApplicationRestartLimitReached,
            NotificationSeverity.Error, TargetType(kind), resourceId.ToString("D"),
            $"Restart limit reached: {resourceName}",
            $"The {label} '{resourceName}' restarted {restartCount} times (limit {maxRestartCount}). Vessel stopped " +
            "it and kept its containers for inspection; redeploy or reset the restart limit to try again.",
            Payload(new Dictionary<string, object?>
            {
                ["resourceKind"] = kind.ToString(),
                ["resourceId"] = resourceId,
                ["restartCount"] = restartCount,
                ["maxRestartCount"] = maxRestartCount
            }),
            Link(kind, resourceId), null);
    }

    public static RaiseNotificationRequest ResourceStoppedUnexpectedly(ResourceRuntimeKind kind, Guid resourceId,
        string resourceName, string serverName)
    {
        var label = KindLabel(kind);
        return new RaiseNotificationRequest(NotificationEventTypes.ApplicationStatusChanged,
            NotificationSeverity.Warning, TargetType(kind), resourceId.ToString("D"),
            $"Resource stopped: {resourceName}",
            $"The {label} '{resourceName}' on server '{serverName}' stopped without a stop from Vessel and was still " +
            "stopped two minutes later.",
            Payload(new Dictionary<string, object?>
            {
                ["resourceKind"] = kind.ToString(),
                ["resourceId"] = resourceId,
                ["status"] = "exited"
            }),
            Link(kind, resourceId), null);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        decimal value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return $"{Math.Round(value, unit == 0 ? 0 : 1).ToString(CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string KindLabel(ResourceRuntimeKind kind)
    {
        return kind switch
        {
            ResourceRuntimeKind.Database => "database",
            ResourceRuntimeKind.Service => "service",
            _ => "application"
        };
    }

    private static NotificationTargetType TargetType(ResourceRuntimeKind kind)
    {
        return kind switch
        {
            ResourceRuntimeKind.Database => NotificationTargetType.Database,
            // Services have no target type of their own; like upstream service applications they notify as
            // applications (the payload carries the resource kind).
            _ => NotificationTargetType.Application
        };
    }

    private static string Link(ResourceRuntimeKind kind, Guid id)
    {
        return kind switch
        {
            ResourceRuntimeKind.Database => $"/databases/{id:D}",
            ResourceRuntimeKind.Service => $"/services/{id:D}",
            _ => $"/applications/{id:D}"
        };
    }

    private static string Payload(Dictionary<string, object?> values)
    {
        return JsonSerializer.Serialize(values);
    }
}
