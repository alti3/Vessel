using Vessel.Domain.Common;

namespace Vessel.Domain.Monitoring;

public enum MetricResolution
{
    /// <summary>One collection (kept 24 hours).</summary>
    Raw = 0,

    /// <summary>Average of the raw samples of one hour (kept 30 days).</summary>
    Hourly = 1
}

/// <summary>
///     One host metrics data point of a server: CPU, memory, the most used probed filesystem, load and network
///     throughput. Raw samples come from one agentless collection (SRV-03); hourly rollups average them.
/// </summary>
public sealed class ServerMetricSample : Entity<ServerMetricSampleId>
{
    public static readonly TimeSpan RawRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan RollupRetention = TimeSpan.FromDays(30);

    private ServerMetricSample()
    {
    }

    private ServerMetricSample(ServerMetricSampleId id, ServerId serverId, MetricResolution resolution,
        DateTimeOffset bucketStart, DateTimeOffset now) : base(id, now)
    {
        ServerId = serverId;
        Resolution = resolution;
        BucketStart = bucketStart;
    }

    public ServerId ServerId { get; private set; }

    public MetricResolution Resolution { get; private set; }

    /// <summary>Collection time of a raw sample, or the start of the hour of a rollup.</summary>
    public DateTimeOffset BucketStart { get; private set; }

    public int SampleCount { get; private set; } = 1;

    public decimal? CpuPercent { get; private set; }

    /// <summary>Highest raw CPU value of a rollup (equal to <see cref="CpuPercent" /> for raw samples).</summary>
    public decimal? CpuPercentMax { get; private set; }

    public int? CpuCount { get; private set; }

    public decimal? Load1 { get; private set; }

    public decimal? Load5 { get; private set; }

    public decimal? Load15 { get; private set; }

    public long? MemoryTotalBytes { get; private set; }

    public long? MemoryUsedBytes { get; private set; }

    /// <summary>Mount point of the most used probed filesystem.</summary>
    public string? DiskMount { get; private set; }

    public long? DiskTotalBytes { get; private set; }

    public long? DiskUsedBytes { get; private set; }

    public long? NetworkRxBytesPerSecond { get; private set; }

    public long? NetworkTxBytesPerSecond { get; private set; }

    public int ContainersRunning { get; private set; }

    public int ContainersTotal { get; private set; }

    public decimal? MemoryPercent => Percent(MemoryUsedBytes, MemoryTotalBytes);

    public decimal? DiskPercent => Percent(DiskUsedBytes, DiskTotalBytes);

    public static ServerMetricSample CreateRaw(ServerId serverId, DateTimeOffset collectedAt, decimal? cpuPercent,
        int? cpuCount, decimal? load1, decimal? load5, decimal? load15, long? memoryTotalBytes, long? memoryUsedBytes,
        string? diskMount, long? diskTotalBytes, long? diskUsedBytes, long? networkRxBytesPerSecond,
        long? networkTxBytesPerSecond, int containersRunning, int containersTotal)
    {
        if (containersRunning < 0 || containersTotal < 0)
            throw new DomainException("Container counts cannot be negative.");
        return new ServerMetricSample(ServerMetricSampleId.New(), serverId, MetricResolution.Raw, collectedAt,
            collectedAt)
        {
            CpuPercent = ClampPercent(cpuPercent),
            CpuPercentMax = ClampPercent(cpuPercent),
            CpuCount = cpuCount is > 0 ? cpuCount : null,
            Load1 = NonNegative(load1),
            Load5 = NonNegative(load5),
            Load15 = NonNegative(load15),
            MemoryTotalBytes = NonNegative(memoryTotalBytes),
            MemoryUsedBytes = NonNegative(memoryUsedBytes),
            DiskMount = DomainValidation.Optional(diskMount, nameof(DiskMount), 255),
            DiskTotalBytes = NonNegative(diskTotalBytes),
            DiskUsedBytes = NonNegative(diskUsedBytes),
            NetworkRxBytesPerSecond = NonNegative(networkRxBytesPerSecond),
            NetworkTxBytesPerSecond = NonNegative(networkTxBytesPerSecond),
            ContainersRunning = containersRunning,
            ContainersTotal = containersTotal
        };
    }

    /// <summary>Averages the raw samples of one server and hour into an hourly rollup.</summary>
    public static ServerMetricSample CreateHourlyRollup(ServerId serverId, DateTimeOffset hourStart,
        IReadOnlyCollection<ServerMetricSample> raw, DateTimeOffset now)
    {
        if (raw.Count == 0) throw new DomainException("A rollup needs at least one raw sample.");
        if (raw.Any(sample => sample.ServerId != serverId || sample.Resolution != MetricResolution.Raw))
            throw new DomainException("A rollup averages raw samples of one server only.");
        var start = TruncateToHour(hourStart);
        ServerMetricSample last = raw.OrderBy(sample => sample.BucketStart).Last();
        return new ServerMetricSample(ServerMetricSampleId.New(), serverId, MetricResolution.Hourly, start, now)
        {
            SampleCount = raw.Count,
            CpuPercent = Average(raw.Select(sample => sample.CpuPercent)),
            CpuPercentMax = raw.Max(sample => sample.CpuPercentMax),
            CpuCount = last.CpuCount,
            Load1 = Average(raw.Select(sample => sample.Load1)),
            Load5 = Average(raw.Select(sample => sample.Load5)),
            Load15 = Average(raw.Select(sample => sample.Load15)),
            MemoryTotalBytes = last.MemoryTotalBytes,
            MemoryUsedBytes = AverageLong(raw.Select(sample => sample.MemoryUsedBytes)),
            DiskMount = last.DiskMount,
            DiskTotalBytes = last.DiskTotalBytes,
            DiskUsedBytes = AverageLong(raw.Select(sample => sample.DiskUsedBytes)),
            NetworkRxBytesPerSecond = AverageLong(raw.Select(sample => sample.NetworkRxBytesPerSecond)),
            NetworkTxBytesPerSecond = AverageLong(raw.Select(sample => sample.NetworkTxBytesPerSecond)),
            ContainersRunning = (int)Math.Round(raw.Average(sample => sample.ContainersRunning)),
            ContainersTotal = (int)Math.Round(raw.Average(sample => sample.ContainersTotal))
        };
    }

    public static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    public static decimal? Percent(long? used, long? total)
    {
        if (used is not { } u || total is not { } t || t <= 0) return null;
        return Math.Round(Math.Clamp((decimal)u * 100m / t, 0m, 100m), 2);
    }

    private static decimal? ClampPercent(decimal? value)
    {
        return value is { } v ? Math.Round(Math.Clamp(v, 0m, 100m), 2) : null;
    }

    private static decimal? NonNegative(decimal? value)
    {
        return value is { } v ? Math.Round(Math.Max(0m, v), 2) : null;
    }

    private static long? NonNegative(long? value)
    {
        return value is { } v ? Math.Max(0L, v) : null;
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        decimal[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : Math.Round(present.Average(), 2);
    }

    private static long? AverageLong(IEnumerable<long?> values)
    {
        long[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : (long)Math.Round(present.Average(value => (decimal)value));
    }
}

/// <summary>One container's resource usage at one raw collection (kept 24 hours).</summary>
public sealed class ContainerMetricSample : Entity<ContainerMetricSampleId>
{
    private ContainerMetricSample()
    {
    }

    private ContainerMetricSample(ContainerMetricSampleId id, ServerId serverId, DateTimeOffset collectedAt)
        : base(id, collectedAt)
    {
        ServerId = serverId;
        CollectedAt = collectedAt;
    }

    public ServerId ServerId { get; private set; }

    public DateTimeOffset CollectedAt { get; private set; }

    public string ContainerId { get; private set; } = string.Empty;

    public string ContainerName { get; private set; } = string.Empty;

    /// <summary>The Vessel resource the container belongs to (from its labels), when known.</summary>
    public ResourceRuntimeKind? ResourceKind { get; private set; }

    public Guid? ResourceId { get; private set; }

    public decimal? CpuPercent { get; private set; }

    public long? MemoryUsedBytes { get; private set; }

    public long? MemoryLimitBytes { get; private set; }

    /// <summary>Received bytes since the container started (cumulative counter).</summary>
    public long? NetworkRxBytes { get; private set; }

    public long? NetworkTxBytes { get; private set; }

    public static ContainerMetricSample Create(ServerId serverId, DateTimeOffset collectedAt, string containerId,
        string containerName, ResourceRuntimeKind? resourceKind, Guid? resourceId, decimal? cpuPercent,
        long? memoryUsedBytes, long? memoryLimitBytes, long? networkRxBytes, long? networkTxBytes)
    {
        return new ContainerMetricSample(ContainerMetricSampleId.New(), serverId, collectedAt)
        {
            ContainerId = DomainValidation.Required(containerId, nameof(ContainerId), 128),
            ContainerName = DomainValidation.Optional(containerName, nameof(ContainerName), 255) ?? string.Empty,
            ResourceKind = resourceId.HasValue ? resourceKind : null,
            ResourceId = resourceKind.HasValue ? resourceId : null,
            // Multi-core containers report more than 100 % (one core = 100 %).
            CpuPercent = cpuPercent is { } cpu ? Math.Round(Math.Max(0m, cpu), 2) : null,
            MemoryUsedBytes = memoryUsedBytes is { } used ? Math.Max(0L, used) : null,
            MemoryLimitBytes = memoryLimitBytes is { } limit ? Math.Max(0L, limit) : null,
            NetworkRxBytes = networkRxBytes is { } rx ? Math.Max(0L, rx) : null,
            NetworkTxBytes = networkTxBytes is { } tx ? Math.Max(0L, tx) : null
        };
    }
}
