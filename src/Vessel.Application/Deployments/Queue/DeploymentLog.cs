using Vessel.Application.Persistence;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments.Queue;

/// <summary>
///     Appends deployment log lines safely when several writers (the runner, the queue, a user cancel) append to the
///     same deployment from different scopes: the next sequence always follows the highest persisted one.
/// </summary>
public static class DeploymentLog
{
    public static DeploymentLogLine Append(IVesselDbContext dbContext, Deployment deployment, string stream,
        string message, DateTimeOffset now)
    {
        var deploymentId = deployment.Id;
        var lastPersisted = dbContext.Deployments
            .Where(item => item.Id == deploymentId)
            .SelectMany(item => item.LogLines)
            .Max(line => (int?)line.Sequence) ?? 0;
        deployment.ObserveLogSequence(lastPersisted);
        deployment.AddLogLine(stream, message, now);
        return deployment.LogLines.MaxBy(line => line.Sequence)!;
    }
}
