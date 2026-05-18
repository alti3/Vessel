namespace Vessel.Domain.Databases;

public enum DatabaseHealthState
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Stopped = 4
}
