namespace Vessel.Domain.Databases;

public enum DatabaseHealthState
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Stopped = 4,

    /// <summary>The container runs and its health check has not passed yet (Docker <c>health: starting</c>).</summary>
    Starting = 5
}
