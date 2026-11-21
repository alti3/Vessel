using Vessel.Application.Docker;
using Vessel.Domain.Servers;

namespace Vessel.Application.Monitoring.HostMetrics;

/// <summary>Usage of one filesystem (<c>df -P</c>).</summary>
public sealed record DiskUsageReading(string MountPoint, string Filesystem, long TotalBytes, long UsedBytes,
    long AvailableBytes)
{
    /// <summary>Used share of the space available to users (what <c>df</c> prints as <c>Use%</c>).</summary>
    public decimal? UsedPercent => UsedBytes + AvailableBytes > 0
        ? Math.Round((decimal)UsedBytes * 100m / (UsedBytes + AvailableBytes), 2)
        : null;
}

/// <summary>Host metrics read from <c>/proc</c> and <c>df</c> over the server's own connection.</summary>
public sealed record HostMetricsReading(
    decimal? CpuPercent,
    int? CpuCount,
    decimal? Load1,
    decimal? Load5,
    decimal? Load15,
    long? MemoryTotalBytes,
    long? MemoryUsedBytes,
    long? NetworkRxBytesPerSecond,
    long? NetworkTxBytesPerSecond,
    IReadOnlyList<DiskUsageReading> Disks)
{
    /// <summary>The most used probed filesystem (the one disk alerts and cleanup thresholds look at).</summary>
    public DiskUsageReading? PrimaryDisk => Disks
        .Where(disk => disk.UsedPercent.HasValue)
        .OrderByDescending(disk => disk.UsedPercent)
        .ThenBy(disk => disk.MountPoint == "/" ? 0 : 1)
        .FirstOrDefault();
}

/// <summary>One container from <c>docker stats --no-stream</c>.</summary>
public sealed record ContainerStatsReading(
    string Id,
    string Name,
    decimal? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    long? NetworkRxBytes,
    long? NetworkTxBytes);

/// <summary>
///     Result of one agentless telemetry collection. <see cref="ContainersComplete" /> is true only when the container
///     inventory command succeeded, so an empty list really means "no containers" (status reconciliation marks missing
///     resources exited only for a complete snapshot).
/// </summary>
public sealed record ServerTelemetry(
    DateTimeOffset CollectedAt,
    HostMetricsReading? Host,
    IReadOnlyList<ContainerStatsReading> Stats,
    IReadOnlyList<ContainerStateReading> Containers,
    bool ContainersComplete);

/// <summary>
///     Collects host and container telemetry of one server without a privileged agent container (ADR-0009): one
///     batched, read-only command over the server's own connection (SSH with the pinned host key, or the control-plane
///     host for host-local servers) reads <c>/proc</c>, <c>df</c>, <c>docker stats --no-stream</c> and a structured
///     container <c>inspect</c>.
/// </summary>
public interface IServerTelemetryCollector
{
    Task<ServerTelemetry> CollectAsync(Server server, CancellationToken cancellationToken = default);
}
