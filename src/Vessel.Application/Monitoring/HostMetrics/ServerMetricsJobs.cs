using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;

namespace Vessel.Application.Monitoring.HostMetrics;

/// <summary>Deletes expired metric rows. The EF implementation uses set-based deletes.</summary>
public interface IServerMetricsPruner
{
    Task<int> PruneAsync(DateTimeOffset rawBefore, DateTimeOffset rollupBefore, DateTimeOffset containersBefore,
        CancellationToken cancellationToken = default);
}

/// <summary>Repository-based pruning (used when no set-based implementation is registered, and in tests).</summary>
public sealed class QueryableServerMetricsPruner(IVesselDbContext dbContext) : IServerMetricsPruner
{
    public async Task<int> PruneAsync(DateTimeOffset rawBefore, DateTimeOffset rollupBefore,
        DateTimeOffset containersBefore, CancellationToken cancellationToken = default)
    {
        ServerMetricSample[] samples = dbContext.ServerMetricSamples
            .Where(sample => (sample.Resolution == MetricResolution.Raw && sample.BucketStart < rawBefore) ||
                             (sample.Resolution == MetricResolution.Hourly && sample.BucketStart < rollupBefore))
            .ToArray();
        ContainerMetricSample[] containers = dbContext.ContainerMetricSamples
            .Where(sample => sample.CollectedAt < containersBefore).ToArray();
        foreach (ServerMetricSample sample in samples) dbContext.ServerMetricSampleRepository.Remove(sample);
        foreach (ContainerMetricSample sample in containers) dbContext.ContainerMetricSampleRepository.Remove(sample);
        await dbContext.SaveChangesAsync(cancellationToken);
        return samples.Length + containers.Length;
    }
}

public sealed record ServerMetricsRetentionResult(int RollupsCreated, int RowsDeleted);

/// <summary>
///     Hourly rollups (averages of the raw samples of each completed hour) and retention: raw samples and container
///     samples are kept 24 hours, hourly rollups 30 days. Rollups are idempotent: an hour that already has a rollup is
///     skipped, so a retried or overlapping run creates nothing twice.
/// </summary>
public sealed class ServerMetricsRetentionService(
    IVesselDbContext dbContext,
    IServerMetricsPruner pruner,
    TimeProvider timeProvider)
{
    /// <summary>How far back completed hours are rolled up (covers a few missed runs).</summary>
    public static readonly TimeSpan RollupWindow = TimeSpan.FromHours(6);

    public async Task<ServerMetricsRetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset currentHour = ServerMetricSample.TruncateToHour(now);
        DateTimeOffset windowStart = currentHour - RollupWindow;
        var existing = dbContext.ServerMetricSamples
            .Where(sample => sample.Resolution == MetricResolution.Hourly && sample.BucketStart >= windowStart)
            .Select(sample => new { sample.ServerId, sample.BucketStart })
            .AsEnumerable()
            .Select(item => (item.ServerId, item.BucketStart.UtcTicks))
            .ToHashSet();
        var groups = dbContext.ServerMetricSamples
            .Where(sample => sample.Resolution == MetricResolution.Raw && sample.BucketStart >= windowStart &&
                             sample.BucketStart < currentHour)
            .AsEnumerable()
            .GroupBy(sample => (sample.ServerId, Hour: ServerMetricSample.TruncateToHour(sample.BucketStart)))
            .Where(group => !existing.Contains((group.Key.ServerId, group.Key.Hour.UtcTicks)))
            .ToArray();
        foreach (var group in groups)
            await dbContext.ServerMetricSampleRepository.AddAsync(
                ServerMetricSample.CreateHourlyRollup(group.Key.ServerId, group.Key.Hour, group.ToArray(), now),
                cancellationToken);
        if (groups.Length > 0) await dbContext.SaveChangesAsync(cancellationToken);

        var deleted = await pruner.PruneAsync(now - ServerMetricSample.RawRetention,
            now - ServerMetricSample.RollupRetention, now - ServerMetricSample.RawRetention, cancellationToken);
        return new ServerMetricsRetentionResult(groups.Length, deleted);
    }
}

/// <summary>
///     Every minute: schedules one <see cref="CollectServerMetricsJob" /> per server whose interval elapsed, staggered by
///     a stable per-server offset so collections spread over the minute instead of hitting every host at once.
/// </summary>
public sealed class ServerMetricsDispatchJob(
    IVesselDbContext dbContext,
    IBackgroundJobDispatcher jobs,
    TimeProvider timeProvider)
{
    public const int StaggerSeconds = 45;

    public Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var settings = dbContext.ServerMonitoringSettings.AsEnumerable().ToDictionary(item => item.Id);
        var scheduled = 0;
        foreach (ServerId serverId in dbContext.Servers.Where(server => server.Status != ServerStatus.Disabled)
                     .Select(server => server.Id).ToArray())
        {
            if (settings.TryGetValue(serverId, out ServerMonitoringSettings? setting))
            {
                if (!setting.MetricsEnabled) continue;
                if (setting.LastMetricsCollectedAt is { } last &&
                    now - last < TimeSpan.FromSeconds(setting.MetricsIntervalSeconds - 5))
                    continue;
            }

            var id = serverId.Value;
            jobs.Schedule<CollectServerMetricsJob>(job => job.RunAsync(id, CancellationToken.None),
                TimeSpan.FromSeconds(StaggerOffset(serverId)));
            scheduled++;
        }

        return Task.FromResult(scheduled);
    }

    public static int StaggerOffset(ServerId serverId)
    {
        var hash = 0;
        foreach (var value in serverId.Value.ToByteArray()) hash = unchecked(hash * 31 + value);
        return (int)((uint)hash % StaggerSeconds);
    }
}

public sealed class CollectServerMetricsJob(ServerMetricsCollectionService collection)
{
    public Task RunAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        return collection.CollectScheduledAsync(new ServerId(serverId), cancellationToken);
    }
}

public sealed class ServerMetricsRetentionJob(ServerMetricsRetentionService retention)
{
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return retention.RunAsync(cancellationToken);
    }
}

public static class ServerMetricsRecurringJobs
{
    public const string DispatchJobId = "servers.metrics.dispatch";
    public const string RetentionJobId = "servers.metrics.retention";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        scheduler.AddOrUpdate<ServerMetricsDispatchJob>(DispatchJobId, job => job.RunAsync(CancellationToken.None),
            "* * * * *");
        scheduler.AddOrUpdate<ServerMetricsRetentionJob>(RetentionJobId, job => job.RunAsync(CancellationToken.None),
            "5 * * * *");
    }
}
