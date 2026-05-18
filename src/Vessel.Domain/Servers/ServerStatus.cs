namespace Vessel.Domain.Servers;

public enum ServerStatus
{
    Unknown = 0,
    PendingValidation = 1,
    Reachable = 2,
    Unreachable = 3,
    Disabled = 4,
    Draining = 5
}
