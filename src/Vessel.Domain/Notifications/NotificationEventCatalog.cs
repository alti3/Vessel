using Vessel.Domain.Common;

namespace Vessel.Domain.Notifications;

public enum NotificationEventCategory
{
    Deployment = 0,
    Backup = 1,
    Server = 2,
    Certificate = 3,
    Proxy = 4,
    Application = 5,
    ScheduledTask = 6,
    Security = 7,
    System = 8
}

/// <summary>How repeated events for the same subject (team + event type + resource) are suppressed.</summary>
public enum NotificationThrottleMode
{
    /// <summary>Every occurrence is delivered.</summary>
    None = 0,

    /// <summary>At most one delivery per <see cref="NotificationThrottlePolicy.Interval" /> for the same subject.</summary>
    Interval = 1,

    /// <summary>
    ///     Delivered once and then suppressed until a recovery event releases it (for example server unreachable until
    ///     the server is reachable again). This is the flapping guard: a resource that keeps failing alerts once.
    /// </summary>
    OncePerIncident = 2
}

public sealed record NotificationThrottlePolicy(NotificationThrottleMode Mode, TimeSpan? Interval)
{
    public static readonly NotificationThrottlePolicy None = new(NotificationThrottleMode.None, null);

    public static readonly NotificationThrottlePolicy OncePerIncident = new(NotificationThrottleMode.OncePerIncident, null);

    public static NotificationThrottlePolicy Every(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new DomainException("Notification throttle interval must be positive.");

        return new NotificationThrottlePolicy(NotificationThrottleMode.Interval, interval);
    }
}

/// <summary>
///     One entry of the notification event catalog.
/// </summary>
/// <param name="Key">Vessel event type (for example <c>deployment.failed</c>).</param>
/// <param name="CoolifyKey">Upstream Coolify toggle name (import mapping); null when upstream has no equivalent.</param>
/// <param name="Category">Grouping used by the UI and by the legacy per-category switches.</param>
/// <param name="DefaultEnabled">Whether new targets (and targets without explicit subscriptions) receive the event.</param>
/// <param name="AlwaysDeliver">Ignores per-target subscriptions and the minimum severity (upstream "always send" events).</param>
/// <param name="DeliverToEmail">False for team-wide notices that upstream never sends by email (the "general" event).</param>
/// <param name="Throttle">Duplicate suppression policy.</param>
/// <param name="Releases">Event key whose throttle is released when this event is raised (recovery events).</param>
/// <param name="RequiresReleasedIncident">Deliver only when <paramref name="Releases" /> actually released an open incident.</param>
public sealed record NotificationEventDefinition(
    string Key,
    string? CoolifyKey,
    NotificationEventCategory Category,
    bool DefaultEnabled,
    bool AlwaysDeliver,
    bool DeliverToEmail,
    NotificationThrottlePolicy Throttle,
    string? Releases = null,
    bool RequiresReleasedIncident = false);

/// <summary>
///     The granular notification event catalog (NTF-05). Each target subscribes to a subset of these keys; targets
///     without explicit subscriptions receive the events whose <see cref="NotificationEventDefinition.DefaultEnabled" />
///     is set, which mirrors upstream Coolify's per-channel defaults (failures on, successes off).
/// </summary>
public static class NotificationEventCatalog
{
    public const string DeploymentSucceeded = "deployment.succeeded";
    public const string DeploymentFailed = "deployment.failed";
    public const string DeploymentCancelled = "deployment.cancelled";
    public const string BackupSucceeded = "backup.succeeded";
    public const string BackupFailed = "backup.failed";
    public const string BackupMissing = "backup.missing";
    public const string RestoreFailed = "backup.restore_failed";
    public const string BackupRecovered = "backup.recovered";
    public const string BackupDestinationUnusable = "backup.destination_unusable";
    public const string ServerUnreachable = "server.unreachable";
    public const string ServerReachable = "server.reachable";
    public const string ServerDiskUsageHigh = "server.disk_usage_high";
    public const string ServerSshHostKeyChanged = "server.ssh_host_key_changed";
    public const string DockerCleanupSucceeded = "server.docker_cleanup_succeeded";
    public const string DockerCleanupFailed = "server.docker_cleanup_failed";
    public const string CertificateIssuanceFailed = "certificate.issuance_failed";
    public const string CertificateRenewalFailed = "certificate.renewal_failed";
    public const string CertificateExpiringSoon = "certificate.expiring_soon";
    public const string ProxyApplyFailed = "proxy.apply_failed";
    public const string ProxyOutdated = "proxy.outdated";
    public const string ApplicationStatusChanged = "application.status_changed";
    public const string ApplicationRestartLimitReached = "application.restart_limit_reached";
    public const string ScheduledTaskSucceeded = "scheduled_task.succeeded";
    public const string ScheduledTaskFailed = "scheduled_task.failed";
    public const string ScheduledTaskRecovered = "scheduled_task.recovered";
    public const string ScheduledJobMissed = "scheduled_job.missed";
    public const string PlatformUpdateAvailable = "platform.update_available";
    public const string PlatformUpdateStarted = "platform.update_started";
    public const string PlatformUpdateFailed = "platform.update_failed";
    public const string PlatformUpdateSucceeded = "platform.update_succeeded";
    public const string ControlPlaneBackupFailed = "platform.control_plane_backup_failed";
    public const string ApiTokenFailing = "api_token.failing";
    public const string ApiTokenExpiring = "api_token.expiring";
    public const string ChannelDisabled = "notification.channel_disabled";
    public const string Test = "notification.test";

    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);

    private static readonly NotificationEventDefinition[] Definitions =
    [
        new(DeploymentSucceeded, "deployment_success", NotificationEventCategory.Deployment, false, false, true,
            NotificationThrottlePolicy.None),
        new(DeploymentFailed, "deployment_failure", NotificationEventCategory.Deployment, true, false, true,
            NotificationThrottlePolicy.None),
        new(DeploymentCancelled, null, NotificationEventCategory.Deployment, false, false, true,
            NotificationThrottlePolicy.None),
        // Backup failures alert on the first failure and then at most daily until a success releases the throttle.
        new(BackupSucceeded, "backup_success", NotificationEventCategory.Backup, false, false, true,
            NotificationThrottlePolicy.None, Releases: BackupFailed),
        new(BackupFailed, "backup_failure", NotificationEventCategory.Backup, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        new(BackupMissing, "backup_failure", NotificationEventCategory.Backup, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        new(RestoreFailed, "backup_failure", NotificationEventCategory.Backup, true, false, true,
            NotificationThrottlePolicy.None),
        // "Backups work again" notice after a failure streak (the producer sends it once per streak).
        new(BackupRecovered, "backup_success", NotificationEventCategory.Backup, true, false, true,
            NotificationThrottlePolicy.None, Releases: BackupFailed),
        // A team backup destination failed its connection test (the producer notifies once until it validates).
        new(BackupDestinationUnusable, "backup_failure", NotificationEventCategory.Backup, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        // Unreachable alerts once per incident; "reachable" is sent only when it closes an alerted incident.
        new(ServerUnreachable, "server_unreachable", NotificationEventCategory.Server, true, false, true,
            NotificationThrottlePolicy.OncePerIncident),
        new(ServerReachable, "server_reachable", NotificationEventCategory.Server, false, false, true,
            NotificationThrottlePolicy.None, Releases: ServerUnreachable, RequiresReleasedIncident: true),
        new(ServerDiskUsageHigh, "server_disk_usage", NotificationEventCategory.Server, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        new(ServerSshHostKeyChanged, "server_unreachable", NotificationEventCategory.Server, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        new(DockerCleanupSucceeded, "docker_cleanup_success", NotificationEventCategory.Server, false, false, true,
            NotificationThrottlePolicy.None, Releases: DockerCleanupFailed),
        new(DockerCleanupFailed, "docker_cleanup_failure", NotificationEventCategory.Server, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        // Certificate problems are upstream "always send" events (ssl_certificate_renewal).
        new(CertificateIssuanceFailed, "ssl_certificate_renewal", NotificationEventCategory.Certificate, true, true,
            true, NotificationThrottlePolicy.Every(Daily)),
        new(CertificateRenewalFailed, "ssl_certificate_renewal", NotificationEventCategory.Certificate, true, true,
            true, NotificationThrottlePolicy.Every(Daily)),
        // An in-use certificate Traefik has not renewed (acme.json sync); raised once per expiry by the producer.
        new(CertificateExpiringSoon, "ssl_certificate_renewal", NotificationEventCategory.Certificate, true, true,
            true, NotificationThrottlePolicy.Every(Daily)),
        new(ProxyApplyFailed, null, NotificationEventCategory.Proxy, true, false, true,
            NotificationThrottlePolicy.Every(TimeSpan.FromHours(1))),
        new(ProxyOutdated, "traefik_outdated", NotificationEventCategory.Proxy, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        // Status changes are flapping-prone: at most one per resource per 15 minutes.
        new(ApplicationStatusChanged, "status_change", NotificationEventCategory.Application, false, false, true,
            NotificationThrottlePolicy.Every(TimeSpan.FromMinutes(15))),
        // Restart loops: at most one notification per resource per hour.
        new(ApplicationRestartLimitReached, "restart_limit_reached", NotificationEventCategory.Application, true,
            false, true, NotificationThrottlePolicy.Every(TimeSpan.FromHours(1))),
        new(ScheduledTaskSucceeded, "scheduled_task_success", NotificationEventCategory.ScheduledTask, false, false,
            true, NotificationThrottlePolicy.None, Releases: ScheduledTaskFailed),
        new(ScheduledTaskFailed, "scheduled_task_failure", NotificationEventCategory.ScheduledTask, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        // "Task works again" notice after a failure streak (upstream ScheduledJobRecovered, task_recovered).
        new(ScheduledTaskRecovered, "scheduled_task_success", NotificationEventCategory.ScheduledTask, true, false,
            true, NotificationThrottlePolicy.None, Releases: ScheduledTaskFailed),
        // Occurrences of a schedule that were not run (control plane down, worker lost); NTF-08.
        new(ScheduledJobMissed, "scheduled_task_failure", NotificationEventCategory.ScheduledTask, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        // Platform (instance) lifecycle: sent to teams that include an instance administrator (OPS-01/OPS-02).
        new(PlatformUpdateAvailable, null, NotificationEventCategory.System, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        new(PlatformUpdateStarted, null, NotificationEventCategory.System, true, false, true,
            NotificationThrottlePolicy.None),
        new(PlatformUpdateFailed, null, NotificationEventCategory.System, true, false, true,
            NotificationThrottlePolicy.None),
        new(PlatformUpdateSucceeded, null, NotificationEventCategory.System, true, false, true,
            NotificationThrottlePolicy.None),
        new(ControlPlaneBackupFailed, null, NotificationEventCategory.System, true, false, true,
            NotificationThrottlePolicy.Every(Daily)),
        new(ApiTokenFailing, "api_token_failing", NotificationEventCategory.Security, true, true, true,
            NotificationThrottlePolicy.Every(Daily)),
        // IAM-04: raised once per token by the expiry-warning job (the token records that it was warned).
        new(ApiTokenExpiring, "api_token_expiring", NotificationEventCategory.Security, true, false, true,
            NotificationThrottlePolicy.None),
        // Upstream "general" notice: other channels only, never email (admins get a separate email).
        new(ChannelDisabled, "general", NotificationEventCategory.System, true, true, false,
            NotificationThrottlePolicy.None),
        new(Test, "test", NotificationEventCategory.System, true, true, true, NotificationThrottlePolicy.None)
    ];

    private static readonly Dictionary<string, NotificationEventDefinition> ByKey =
        Definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    public static IReadOnlyList<NotificationEventDefinition> All => Definitions;

    /// <summary>Event keys an operator can subscribe to (always-delivered events are excluded).</summary>
    public static IReadOnlyList<NotificationEventDefinition> Subscribable =>
        Definitions.Where(definition => !definition.AlwaysDeliver).ToArray();

    public static IReadOnlyList<string> DefaultSubscriptions =>
        Subscribable.Where(definition => definition.DefaultEnabled).Select(definition => definition.Key).ToArray();

    public static NotificationEventDefinition? Find(string? eventType)
    {
        return eventType is not null && ByKey.TryGetValue(eventType, out var definition) ? definition : null;
    }

    public static bool IsKnown(string? eventType)
    {
        return Find(eventType) is not null;
    }
}
