using Vessel.Domain;

namespace Vessel.Application.Deployments;

public sealed class RunDeploymentJob(IDeploymentRunner runner)
{
    public Task RunAsync(Guid deploymentId, CancellationToken cancellationToken = default)
    {
        return runner.RunAsync(new DeploymentId(deploymentId), cancellationToken);
    }

    /// <summary>Retry of a deployment whose run lock was held by another run (the deployment stays queued).</summary>
    public Task RunAttemptAsync(Guid deploymentId, int attempt, CancellationToken cancellationToken = default)
    {
        return runner.RunAsync(new DeploymentId(deploymentId), attempt, cancellationToken);
    }
}
