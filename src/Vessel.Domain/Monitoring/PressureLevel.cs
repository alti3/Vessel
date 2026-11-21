namespace Vessel.Domain.Monitoring;

/// <summary>Resource pressure of one metric (upstream Coolify <c>PressureLevel</c>).</summary>
public enum PressureLevel
{
    Unknown = 0,
    Normal = 1,
    Warning = 2,
    Critical = 3
}

/// <summary>
///     Pressure thresholds: CPU and memory warn at 70 % and are critical at 90 %; disk warns at 80 % and is critical
///     at 90 % (upstream Coolify v4.5 fleet metrics).
/// </summary>
public static class PressureThresholds
{
    public const decimal CpuWarning = 70m;
    public const decimal CpuCritical = 90m;
    public const decimal MemoryWarning = 70m;
    public const decimal MemoryCritical = 90m;
    public const decimal DiskWarning = 80m;
    public const decimal DiskCritical = 90m;

    public static PressureLevel ForCpu(decimal? percent)
    {
        return Level(percent, CpuWarning, CpuCritical);
    }

    public static PressureLevel ForMemory(decimal? percent)
    {
        return Level(percent, MemoryWarning, MemoryCritical);
    }

    public static PressureLevel ForDisk(decimal? percent)
    {
        return Level(percent, DiskWarning, DiskCritical);
    }

    public static PressureLevel Worst(params PressureLevel[] levels)
    {
        return levels.Length == 0 ? PressureLevel.Unknown : levels.Max();
    }

    private static PressureLevel Level(decimal? percent, decimal warning, decimal critical)
    {
        if (percent is not { } value) return PressureLevel.Unknown;
        if (value >= critical) return PressureLevel.Critical;
        return value >= warning ? PressureLevel.Warning : PressureLevel.Normal;
    }
}
