namespace Vessel.Domain.Databases;

public enum DatabaseLifecycleState
{
    NotProvisioned = 0,
    Provisioning = 1,
    Running = 2,
    Stopped = 3,
    Restarting = 4,
    Deleting = 5,
    Deleted = 6,
    Failed = 7
}
