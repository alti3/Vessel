using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Common;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;

namespace Vessel.Application.Monitoring.HostMetrics;

public sealed record MetricPointDto(
    DateTimeOffset Time,
    string Resolution,
    int SampleCount,
    decimal? CpuPercent,
    decimal? CpuPercentMax,
    int? CpuCount,
    decimal? Load1,
    decimal? Load5,
    decimal? Load15,
    long? MemoryTotalBytes,
    long? MemoryUsedBytes,
    decimal? MemoryPercent,
    string? DiskMount,
    long? DiskTotalBytes,
    long? DiskUsedBytes,
    decimal? DiskPercent,
    long? NetworkRxBytesPerSecond,
    long? NetworkTxBytesPerSecond,
    int ContainersRunning,
    int ContainersTotal);

public sealed record PressureDto(string Cpu, string Memory, string Disk, string Overall);

public sealed record ContainerMetricDto(
    Guid ServerId,
    string ContainerId,
    string ContainerName,
    string? ResourceKind,
    Guid? ResourceId,
    decimal? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    long? NetworkRxBytes,
    long? NetworkTxBytes,
    DateTimeOffset CollectedAt);

public sealed record ServerMetricsDto(
    Guid ServerId,
    DateTimeOffset From,
    DateTimeOffset To,
    string Resolution,
    MetricPointDto? Latest,
    PressureDto Pressure,
    IReadOnlyList<MetricPointDto> Points,
    IReadOnlyList<ContainerMetricDto> Containers);

public sealed record FleetServerMetricsDto(
    Guid ServerId,
    string Name,
    string Status,
    MetricPointDto? Latest,
    PressureDto Pressure,
    bool Stale);

public sealed record FleetMetricsDto(
    DateTimeOffset GeneratedAt,
    int ServerCount,
    int ReportingServers,
    decimal? AverageCpuPercent,
    decimal? AverageMemoryPercent,
    decimal? MaxDiskPercent,
    int ContainersRunning,
    IReadOnlyList<FleetServerMetricsDto> Servers,
    IReadOnlyList<ContainerMetricDto> TopContainersByCpu,
    IReadOnlyList<ContainerMetricDto> TopContainersByMemory);

/// <summary>Server and fleet metric queries (SRV-03). Read access to the server is required; ranges are capped at 30 days.</summary>
public sealed class ServerMetricsQueryService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    TimeProvider timeProvider)
{
    public const int TopContainers = 20;
    public static readonly TimeSpan MaxRange = ServerMetricSample.RollupRetention;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Metric series of one server. Resolution is "raw", "hourly", or null/"auto" (raw when the range is within the
    ///     raw retention); the default range is the last hour.
    /// </summary>
    public ServerMetricsDto GetServerMetrics(UserId actorUserId, TeamId teamId, ServerId serverId,
        DateTimeOffset? from, DateTimeOffset? to, string? resolution)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset end = to ?? now;
        DateTimeOffset start = from ?? end - TimeSpan.FromHours(1);
        if (start >= end) throw new DomainException("The metrics range start must be before its end.");
        if (end - start > MaxRange) throw new DomainException("The metrics range cannot exceed 30 days.");

        MetricResolution chosen = resolution?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => start >= now - ServerMetricSample.RawRetention
                ? MetricResolution.Raw
                : MetricResolution.Hourly,
            "raw" => MetricResolution.Raw,
            "hourly" => MetricResolution.Hourly,
            _ => throw new DomainException("Resolution must be auto, raw or hourly.")
        };

        MetricPointDto[] points = dbContext.ServerMetricSamples
            .Where(sample => sample.ServerId == serverId && sample.Resolution == chosen &&
                             sample.BucketStart >= start && sample.BucketStart <= end)
            .OrderBy(sample => sample.BucketStart)
            .Take(5000)
            .AsEnumerable()
            .Select(ToPoint)
            .ToArray();
        ServerMetricSample? latest = Latest(serverId);
        return new ServerMetricsDto(serverId.Value, start, end, chosen.ToString().ToLowerInvariant(),
            latest is null ? null : ToPoint(latest), Pressure(latest), points, LatestContainers([serverId]));
    }

    /// <summary>All servers of the active team the actor can read, with KPIs and the top containers.</summary>
    public FleetMetricsDto GetFleet(UserId actorUserId, TeamId teamId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        Server[] servers = dbContext.Servers.Where(server => server.TeamId == teamId).AsEnumerable()
            .Where(server => authorization.IsAuthorized(actorUserId, teamId, ResourceRef.Server(server.Id),
                VesselPermissions.ServersRead))
            .OrderBy(server => server.Name.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rows = servers.Select(server =>
        {
            ServerMetricSample? latest = Latest(server.Id);
            return new FleetServerMetricsDto(server.Id.Value, server.Name.Value, server.Status.ToString(),
                latest is null ? null : ToPoint(latest), Pressure(latest),
                latest is null || now - latest.BucketStart > StaleAfter);
        }).ToArray();
        var reporting = rows.Where(row => row is { Latest: not null, Stale: false }).ToArray();
        IReadOnlyList<ContainerMetricDto> containers = LatestContainers(servers.Select(server => server.Id).ToArray());
        return new FleetMetricsDto(now, rows.Length, reporting.Length,
            Average(reporting.Select(row => row.Latest!.CpuPercent)),
            Average(reporting.Select(row => row.Latest!.MemoryPercent)),
            reporting.Select(row => row.Latest!.DiskPercent).Where(value => value.HasValue).Max(),
            reporting.Sum(row => row.Latest!.ContainersRunning),
            rows,
            containers.Where(item => item.CpuPercent.HasValue).OrderByDescending(item => item.CpuPercent)
                .Take(TopContainers).ToArray(),
            containers.Where(item => item.MemoryUsedBytes.HasValue).OrderByDescending(item => item.MemoryUsedBytes)
                .Take(TopContainers).ToArray());
    }

    public static MetricPointDto ToPoint(ServerMetricSample sample)
    {
        return new MetricPointDto(sample.BucketStart, sample.Resolution.ToString().ToLowerInvariant(),
            sample.SampleCount, sample.CpuPercent, sample.CpuPercentMax, sample.CpuCount, sample.Load1,
            sample.Load5, sample.Load15, sample.MemoryTotalBytes, sample.MemoryUsedBytes, sample.MemoryPercent,
            sample.DiskMount, sample.DiskTotalBytes, sample.DiskUsedBytes, sample.DiskPercent,
            sample.NetworkRxBytesPerSecond, sample.NetworkTxBytesPerSecond, sample.ContainersRunning,
            sample.ContainersTotal);
    }

    public static PressureDto Pressure(ServerMetricSample? sample)
    {
        PressureLevel cpu = PressureThresholds.ForCpu(sample?.CpuPercent);
        PressureLevel memory = PressureThresholds.ForMemory(sample?.MemoryPercent);
        PressureLevel disk = PressureThresholds.ForDisk(sample?.DiskPercent);
        return new PressureDto(cpu.ToString(), memory.ToString(), disk.ToString(),
            PressureThresholds.Worst(cpu, memory, disk).ToString());
    }

    private ServerMetricSample? Latest(ServerId serverId)
    {
        return dbContext.ServerMetricSamples
            .Where(sample => sample.ServerId == serverId && sample.Resolution == MetricResolution.Raw)
            .OrderByDescending(sample => sample.BucketStart)
            .FirstOrDefault();
    }

    private List<ContainerMetricDto> LatestContainers(IReadOnlyList<ServerId> serverIds)
    {
        var result = new List<ContainerMetricDto>();
        foreach (ServerId serverId in serverIds)
        {
            DateTimeOffset? latest = dbContext.ContainerMetricSamples
                .Where(sample => sample.ServerId == serverId)
                .OrderByDescending(sample => sample.CollectedAt)
                .Select(sample => (DateTimeOffset?)sample.CollectedAt)
                .FirstOrDefault();
            if (latest is null) continue;
            result.AddRange(dbContext.ContainerMetricSamples
                .Where(sample => sample.ServerId == serverId && sample.CollectedAt == latest.Value)
                .AsEnumerable()
                .Select(sample => new ContainerMetricDto(sample.ServerId.Value, sample.ContainerId,
                    sample.ContainerName, sample.ResourceKind?.ToString(), sample.ResourceId, sample.CpuPercent,
                    sample.MemoryUsedBytes, sample.MemoryLimitBytes, sample.NetworkRxBytes, sample.NetworkTxBytes,
                    sample.CollectedAt)));
        }

        return result;
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        decimal[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : Math.Round(present.Average(), 2);
    }
}
