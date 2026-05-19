namespace Vessel.Application.Deployments;

public sealed class RunDeploymentJob(IDeploymentRunner runner)
{
    public Task RunAsync(Guid deploymentId, CancellationToken cancellationToken = default)
    {
        return runner.RunAsync(new Domain.DeploymentId(deploymentId), cancellationToken);
    }
}
