namespace Vessel.Domain.Deployments;

public enum DeploymentStatus
{
    Queued = 0,
    InProgress = 1,
    Succeeded = 2,
    Failed = 3,
    CanceledByUser = 4
}
