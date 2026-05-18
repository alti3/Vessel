using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

public readonly record struct ResourceLimits
{
    public ResourceLimits(decimal? cpus, long? memoryBytes)
    {
        if (cpus is <= 0) throw new DomainException("CPU limit must be greater than zero.");

        if (memoryBytes is <= 0) throw new DomainException("Memory limit must be greater than zero.");

        Cpus = cpus;
        MemoryBytes = memoryBytes;
    }

    public decimal? Cpus { get; }

    public long? MemoryBytes { get; }

    public static ResourceLimits Unbounded => new(null, null);
}
