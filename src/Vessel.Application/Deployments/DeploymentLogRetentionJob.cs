using Vessel.Application.Jobs;

namespace Vessel.Application.Deployments;

public sealed class DeploymentLogRetentionJob(DeploymentLogRetentionService retentionService)
{
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return retentionService.PruneAsync(DeploymentLogRetentionOptions.Default, cancellationToken);
    }
}

public static class DeploymentLogRecurringJobs
{
    public const string PruneJobId = "deployments.logs.prune";
    public const string PruneCronExpression = "17 3 * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);

        scheduler.AddOrUpdate<DeploymentLogRetentionJob>(
            PruneJobId,
            job => job.RunAsync(CancellationToken.None),
            PruneCronExpression);
    }
}
