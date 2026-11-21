using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

/// <summary>
///     Docker cleanup policy of one server (upstream Coolify <c>docker_cleanup_frequency</c>,
///     <c>docker_cleanup_threshold</c>, <c>force_docker_cleanup</c>, <c>delete_unused_volumes</c>,
///     <c>delete_unused_networks</c>). Volumes are never deleted unless an operator opts in, and even then only
///     anonymous volumes are pruned.
/// </summary>
public sealed record DockerCleanupPolicy(
    bool ScheduleEnabled,
    string CronExpression,
    int ThresholdPercent,
    bool ForceScheduledCleanup,
    bool DeleteUnusedVolumes,
    bool DeleteUnusedNetworks)
{
    public const string DefaultCron = "0 0 * * *";
    public const int DefaultThresholdPercent = 80;

    public static DockerCleanupPolicy Default { get; } =
        new(true, DefaultCron, DefaultThresholdPercent, true, false, false);

    public static DockerCleanupPolicy Create(bool scheduleEnabled, string cronExpression, int thresholdPercent,
        bool forceScheduledCleanup, bool deleteUnusedVolumes, bool deleteUnusedNetworks)
    {
        var cron = DomainValidation.Required(cronExpression, nameof(CronExpression), 120);
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is < 5 or > 6)
            throw new DomainException("Docker cleanup cron expression must contain five (or six) fields.");
        if (cron.Any(character => !(char.IsAsciiDigit(character) || character is ' ' or '*' or '/' or ',' or '-')))
            throw new DomainException("Docker cleanup cron expression contains unsupported characters.");
        if (thresholdPercent is < 1 or > 99)
            throw new DomainException("Docker cleanup threshold must be between 1 and 99 percent.");
        return new DockerCleanupPolicy(scheduleEnabled, string.Join(' ', fields), thresholdPercent,
            forceScheduledCleanup, deleteUnusedVolumes, deleteUnusedNetworks);
    }
}

public enum DiskAlertDecision
{
    /// <summary>Usage is below the threshold and the alert is not re-armed (nothing changes).</summary>
    None = 0,

    /// <summary>Usage is at or above the threshold and an alert is due.</summary>
    Alert = 1,

    /// <summary>Usage is above the threshold, but an alert was sent within the interval.</summary>
    Suppressed = 2,

    /// <summary>Usage dropped at least <see cref="ServerMonitoringSettings.DiskAlertRecoveryMargin" /> points below the threshold.</summary>
    Rearmed = 3
}

/// <summary>
///     Per-server monitoring settings and alert state: metrics collection, disk-usage alert threshold and interval
///     (SRV-08, upstream <c>server_disk_usage_notification_threshold</c> /
///     <c>server_disk_usage_notification_interval_hours</c>), the Docker cleanup policy (SRV-07) and the consecutive
///     failed health checks behind the unreachable alert. A server without a row uses the defaults.
/// </summary>
public sealed class ServerMonitoringSettings : Entity<ServerId>
{
    public const int DefaultMetricsIntervalSeconds = 60;
    public const int MinMetricsIntervalSeconds = 30;
    public const int MaxMetricsIntervalSeconds = 3600;
    public const int DefaultDiskAlertThresholdPercent = 80;
    public const int DefaultDiskAlertIntervalHours = 24;
    public const int MaxDiskAlertIntervalHours = 24 * 7;

    /// <summary>Usage must drop this many points below the threshold before a new alert can fire early (upstream: 5).</summary>
    public const int DiskAlertRecoveryMargin = 5;

    private ServerMonitoringSettings()
    {
    }

    private ServerMonitoringSettings(ServerId serverId, DateTimeOffset now) : base(serverId, now)
    {
    }

    public ServerId ServerId => Id;

    public bool MetricsEnabled { get; private set; } = true;

    public int MetricsIntervalSeconds { get; private set; } = DefaultMetricsIntervalSeconds;

    public int DiskAlertThresholdPercent { get; private set; } = DefaultDiskAlertThresholdPercent;

    public int DiskAlertIntervalHours { get; private set; } = DefaultDiskAlertIntervalHours;

    /// <summary>True until an alert fires; re-armed when usage recovers below the threshold minus the margin.</summary>
    public bool DiskAlertArmed { get; private set; } = true;

    public DateTimeOffset? LastDiskAlertAt { get; private set; }

    public DockerCleanupPolicy DockerCleanup { get; private set; } = DockerCleanupPolicy.Default;

    public DateTimeOffset? LastDockerCleanupAt { get; private set; }

    public DateTimeOffset? LastMetricsCollectedAt { get; private set; }

    public DateTimeOffset? LastMetricsFailureAt { get; private set; }

    public static ServerMonitoringSettings CreateDefault(ServerId serverId, DateTimeOffset now)
    {
        return new ServerMonitoringSettings(serverId, now);
    }

    public void ConfigureMetrics(bool enabled, int intervalSeconds, DateTimeOffset now)
    {
        if (intervalSeconds is < MinMetricsIntervalSeconds or > MaxMetricsIntervalSeconds)
            throw new DomainException(
                $"Metrics interval must be between {MinMetricsIntervalSeconds} and {MaxMetricsIntervalSeconds} seconds.");
        MetricsEnabled = enabled;
        MetricsIntervalSeconds = intervalSeconds;
        Touch(now);
    }

    public void ConfigureDiskAlerts(int thresholdPercent, int intervalHours, DateTimeOffset now)
    {
        if (thresholdPercent is < 1 or > 99)
            throw new DomainException("Disk usage alert threshold must be between 1 and 99 percent.");
        if (intervalHours is < 1 or > MaxDiskAlertIntervalHours)
            throw new DomainException(
                $"Disk usage alert interval must be between 1 and {MaxDiskAlertIntervalHours} hours.");
        if (thresholdPercent != DiskAlertThresholdPercent) DiskAlertArmed = true;
        DiskAlertThresholdPercent = thresholdPercent;
        DiskAlertIntervalHours = intervalHours;
        Touch(now);
    }

    public void ConfigureDockerCleanup(DockerCleanupPolicy policy, DateTimeOffset now)
    {
        DockerCleanup = policy ?? throw new ArgumentNullException(nameof(policy));
        Touch(now);
    }

    /// <summary>
    ///     Applies the disk alert rule to one measurement: alert at or above the threshold at most once per interval,
    ///     unless usage recovered by the margin in between (then the next breach alerts at once). The caller sends the
    ///     notification only for <see cref="DiskAlertDecision.Alert" />.
    /// </summary>
    public DiskAlertDecision EvaluateDiskUsage(decimal usagePercent, DateTimeOffset now)
    {
        if (usagePercent >= DiskAlertThresholdPercent)
        {
            var intervalElapsed = LastDiskAlertAt is not { } last ||
                                  now - last >= TimeSpan.FromHours(DiskAlertIntervalHours);
            if (!DiskAlertArmed && !intervalElapsed) return DiskAlertDecision.Suppressed;
            DiskAlertArmed = false;
            LastDiskAlertAt = now;
            Touch(now);
            return DiskAlertDecision.Alert;
        }

        if (!DiskAlertArmed && usagePercent <= DiskAlertThresholdPercent - DiskAlertRecoveryMargin)
        {
            DiskAlertArmed = true;
            Touch(now);
            return DiskAlertDecision.Rearmed;
        }

        return DiskAlertDecision.None;
    }

    public void RecordMetricsCollected(DateTimeOffset now)
    {
        LastMetricsCollectedAt = now;
        LastMetricsFailureAt = null;
        UpdatedAt = now;
    }

    public void RecordMetricsFailure(DateTimeOffset now)
    {
        LastMetricsFailureAt = now;
        UpdatedAt = now;
    }

    public void RecordDockerCleanup(DateTimeOffset now)
    {
        LastDockerCleanupAt = now;
        UpdatedAt = now;
    }
}
