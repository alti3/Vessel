using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Monitoring.HostMetrics;

public sealed record ServerMonitoringSettingsDto(
    Guid ServerId,
    bool MetricsEnabled,
    int MetricsIntervalSeconds,
    int DiskAlertThresholdPercent,
    int DiskAlertIntervalHours,
    bool DiskAlertArmed,
    DateTimeOffset? LastDiskAlertAt,
    DateTimeOffset? LastMetricsCollectedAt,
    DateTimeOffset? LastMetricsFailureAt);

public sealed record UpdateServerMonitoringSettingsRequest(
    bool? MetricsEnabled = null,
    int? MetricsIntervalSeconds = null,
    int? DiskAlertThresholdPercent = null,
    int? DiskAlertIntervalHours = null);

/// <summary>Per-server metrics collection and disk-usage alert settings (SRV-03, SRV-08).</summary>
public sealed class ServerMonitoringSettingsService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public ServerMonitoringSettingsDto Get(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        return ToDto(dbContext.ServerMonitoringSettings.SingleOrDefault(item => item.Id == serverId) ??
                     ServerMonitoringSettings.CreateDefault(serverId, timeProvider.GetUtcNow()));
    }

    public async Task<ServerMonitoringSettingsDto> UpdateAsync(UserId actorUserId, TeamId teamId, ServerId serverId,
        UpdateServerMonitoringSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        DateTimeOffset now = timeProvider.GetUtcNow();
        ServerMonitoringSettings? settings = dbContext.ServerMonitoringSettings.SingleOrDefault(item => item.Id == serverId);
        if (settings is null)
        {
            settings = ServerMonitoringSettings.CreateDefault(serverId, now);
            await dbContext.ServerMonitoringSettingsRepository.AddAsync(settings, cancellationToken);
        }

        settings.ConfigureMetrics(request.MetricsEnabled ?? settings.MetricsEnabled,
            request.MetricsIntervalSeconds ?? settings.MetricsIntervalSeconds, now);
        settings.ConfigureDiskAlerts(request.DiskAlertThresholdPercent ?? settings.DiskAlertThresholdPercent,
            request.DiskAlertIntervalHours ?? settings.DiskAlertIntervalHours, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.MonitoringSettingsUpdated,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["metricsEnabled"] = settings.MetricsEnabled,
                ["metricsIntervalSeconds"] = settings.MetricsIntervalSeconds,
                ["diskAlertThresholdPercent"] = settings.DiskAlertThresholdPercent,
                ["diskAlertIntervalHours"] = settings.DiskAlertIntervalHours
            }, cancellationToken);
        return ToDto(settings);
    }

    private static ServerMonitoringSettingsDto ToDto(ServerMonitoringSettings settings)
    {
        return new ServerMonitoringSettingsDto(settings.Id.Value, settings.MetricsEnabled,
            settings.MetricsIntervalSeconds, settings.DiskAlertThresholdPercent, settings.DiskAlertIntervalHours,
            settings.DiskAlertArmed, settings.LastDiskAlertAt, settings.LastMetricsCollectedAt,
            settings.LastMetricsFailureAt);
    }
}
