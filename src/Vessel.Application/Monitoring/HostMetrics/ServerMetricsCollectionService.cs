using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Redis;
using Vessel.Application.Resources.Status;
using Vessel.Application.Servers.Alerts;
using Vessel.Application.Servers.DockerCleanup;
using Vessel.Domain;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;

namespace Vessel.Application.Monitoring.HostMetrics;

public sealed record ServerMetricsCollectionResult(Guid ServerId, bool Collected, string Message);

/// <summary>
///     Collects one server's telemetry (SRV-03) and feeds the producers that depend on it: the raw metric sample and
///     container samples, resource runtime status reconciliation (SRV-04), the disk-usage alert (SRV-08) and the
///     disk-threshold Docker cleanup (SRV-07). Collection is single-flight per server, has a short timeout, and backs off
///     after a failure so an unreachable server never blocks the fleet.
/// </summary>
public sealed class ServerMetricsCollectionService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IServerTelemetryCollector collector,
    IDistributedLockManager locks,
    ResourceStatusReconciler reconciler,
    ServerAlertEvaluator alerts,
    IBackgroundJobDispatcher jobs,
    IRealtimeNotifier realtime,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan CollectionTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan ThresholdCleanupCooldown = TimeSpan.FromHours(1);

    public static string LockKey(ServerId serverId)
    {
        return $"servers:metrics:{serverId.Value:D}";
    }

    /// <summary>Operator-triggered collection: needs server write access (and an instance admin for host-local servers).</summary>
    public async Task<ServerMetricsCollectionResult> CollectAsync(UserId actorUserId, TeamId teamId,
        ServerId serverId, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = dbContext.Servers.Single(item => item.Id == serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        return await CollectCoreAsync(server, actorUserId, cancellationToken);
    }

    /// <summary>Scheduled collection (no actor). Honours the metrics switch and the failure cooldown.</summary>
    public async Task<ServerMetricsCollectionResult> CollectScheduledAsync(ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        Server? server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId);
        if (server is null || server.Status == ServerStatus.Disabled)
            return new ServerMetricsCollectionResult(serverId.Value, false, "Server is missing or disabled.");
        ServerMonitoringSettings? settings = dbContext.ServerMonitoringSettings.SingleOrDefault(item => item.Id == serverId);
        if (settings is { MetricsEnabled: false })
            return new ServerMetricsCollectionResult(serverId.Value, false, "Metrics collection is disabled.");
        if (settings?.LastMetricsFailureAt is { } failedAt && timeProvider.GetUtcNow() - failedAt < FailureCooldown)
            return new ServerMetricsCollectionResult(serverId.Value, false, "Collection is cooling down after a failure.");
        return await CollectCoreAsync(server, null, cancellationToken);
    }

    private async Task<ServerMetricsCollectionResult> CollectCoreAsync(Server server, UserId? actorUserId,
        CancellationToken cancellationToken)
    {
        if (!HostLocalExecutionGuard.IsAuthorized(authorization, server, actorUserId))
            return new ServerMetricsCollectionResult(server.Id.Value, false,
                HostLocalExecutionGuard.UnattendedNotAuthorizedMessage);

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(LockKey(server.Id),
            CollectionTimeout + TimeSpan.FromSeconds(30), TimeSpan.Zero, cancellationToken);
        if (handle is null)
            return new ServerMetricsCollectionResult(server.Id.Value, false, "A collection is already running.");

        ServerMonitoringSettings settings = await SettingsAsync(server.Id, cancellationToken);
        ServerTelemetry telemetry;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(CollectionTimeout);
            try
            {
                telemetry = await collector.CollectAsync(server, timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                settings.RecordMetricsFailure(timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(CancellationToken.None);
                return new ServerMetricsCollectionResult(server.Id.Value, false,
                    "Telemetry could not be collected from the server.");
            }
        }

        ServerMetricSample? sample = await PersistAsync(server, telemetry, cancellationToken);
        settings.RecordMetricsCollected(telemetry.CollectedAt);
        DiskUsageReading? disk = telemetry.Host?.PrimaryDisk;
        await alerts.EvaluateDiskAsync(server, settings, disk?.UsedPercent, disk?.MountPoint, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await reconciler.ReconcileAsync(server, telemetry.Containers, telemetry.ContainersComplete,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Status reconciliation must not lose the metrics sample; the next collection retries it.
        }

        TriggerThresholdCleanup(server, settings, disk?.UsedPercent);
        if (sample is not null)
            await PublishAsync(server, sample, cancellationToken);
        return new ServerMetricsCollectionResult(server.Id.Value, true, "Metrics collected.");
    }

    private async Task<ServerMetricSample?> PersistAsync(Server server, ServerTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        var running = telemetry.Containers.Count(container =>
            string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase));
        ServerMetricSample? sample = null;
        if (telemetry.Host is { } host)
        {
            DiskUsageReading? disk = host.PrimaryDisk;
            sample = ServerMetricSample.CreateRaw(server.Id, telemetry.CollectedAt, host.CpuPercent, host.CpuCount,
                host.Load1, host.Load5, host.Load15, host.MemoryTotalBytes, host.MemoryUsedBytes, disk?.MountPoint,
                disk is null ? null : disk.UsedBytes + disk.AvailableBytes, disk?.UsedBytes,
                host.NetworkRxBytesPerSecond, host.NetworkTxBytesPerSecond,
                telemetry.ContainersComplete ? running : telemetry.Stats.Count,
                telemetry.ContainersComplete ? telemetry.Containers.Count : telemetry.Stats.Count);
            await dbContext.ServerMetricSampleRepository.AddAsync(sample, cancellationToken);
        }

        var serviceProjects = dbContext.ServiceResources.Where(service => service.ServerId == server.Id)
            .AsEnumerable()
            .ToDictionary(service => RuntimeResourceLabels.ServiceProjectName(service.Id.Value),
                service => service.Id.Value, StringComparer.Ordinal);
        foreach (ContainerStatsReading stats in telemetry.Stats)
        {
            ContainerStateReading? state = telemetry.Containers.FirstOrDefault(container =>
                container.Id.StartsWith(stats.Id, StringComparison.Ordinal) ||
                string.Equals(container.Name, stats.Name, StringComparison.Ordinal));
            RuntimeResourceKey? key = state is null ? null : RuntimeResourceLabels.Resolve(state.Labels, serviceProjects);
            await dbContext.ContainerMetricSampleRepository.AddAsync(ContainerMetricSample.Create(server.Id,
                telemetry.CollectedAt, stats.Id, stats.Name, key?.Kind, key?.Id, stats.CpuPercent,
                stats.MemoryUsedBytes, stats.MemoryLimitBytes, stats.NetworkRxBytes, stats.NetworkTxBytes),
                cancellationToken);
        }

        return sample;
    }

    private void TriggerThresholdCleanup(Server server, ServerMonitoringSettings settings, decimal? usagePercent)
    {
        if (usagePercent is not { } usage || usage < settings.DockerCleanup.ThresholdPercent) return;
        if (settings.LastDockerCleanupAt is { } last && timeProvider.GetUtcNow() - last < ThresholdCleanupCooldown)
            return;
        var serverId = server.Id.Value;
        jobs.Enqueue<RunDockerCleanupJob>(job =>
            job.RunAsync(serverId, DockerCleanupTrigger.DiskThreshold, CancellationToken.None));
    }

    private async Task PublishAsync(Server server, ServerMetricSample sample, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishAsync(new RealtimeGroup(RealtimeGroupKind.Server, server.Id.Value.ToString("D")),
                new RealtimeMessage("server.metrics", ServerMetricsQueryService.ToPoint(sample)), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Realtime delivery is best effort.
        }
    }

    private async Task<ServerMonitoringSettings> SettingsAsync(ServerId serverId, CancellationToken cancellationToken)
    {
        ServerMonitoringSettings? settings = dbContext.ServerMonitoringSettings.SingleOrDefault(item => item.Id == serverId);
        if (settings is not null) return settings;
        settings = ServerMonitoringSettings.CreateDefault(serverId, timeProvider.GetUtcNow());
        await dbContext.ServerMonitoringSettingsRepository.AddAsync(settings, cancellationToken);
        return settings;
    }
}
