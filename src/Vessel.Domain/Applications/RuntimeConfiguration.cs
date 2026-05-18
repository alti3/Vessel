using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

public readonly record struct RuntimeConfiguration(
    PortNumber? ExposedPort,
    ResourceLimits Limits,
    bool HealthCheckEnabled,
    string HealthCheckPath)
{
    public static RuntimeConfiguration Default => new(null, ResourceLimits.Unbounded, true, "/");
}
