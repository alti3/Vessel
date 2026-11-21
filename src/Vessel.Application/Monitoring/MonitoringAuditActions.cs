namespace Vessel.Application.Monitoring;

/// <summary>Audit actions of server monitoring, runtime status, runtime logs and Docker cleanup.</summary>
public static class MonitoringAuditActions
{
    public const string MonitoringSettingsUpdated = "server.monitoring_settings_updated";
    public const string MetricsCollected = "server.metrics_collected";
    public const string DockerCleanupPolicyUpdated = "server.docker_cleanup_policy_updated";
    public const string DockerCleanupRequested = "server.docker_cleanup_requested";
    public const string DockerCleanupFinished = "server.docker_cleanup_finished";
    public const string ImageDeleted = "server.image_deleted";
    public const string ResourceRestartLimitUpdated = "resource.restart_limit_updated";
    public const string ResourceRestartLimitReset = "resource.restart_limit_reset";
    public const string RuntimeLogsRead = "resource.runtime_logs_read";
    public const string RuntimeLogsExported = "resource.runtime_logs_exported";
}
