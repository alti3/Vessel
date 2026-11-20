using Vessel.Application.Jobs;
using Vessel.Domain;

namespace Vessel.Application.Deployments.Queue;

/// <summary>
///     Thin Hangfire entry points of the deployment queue: delayed queue advancement, the recurring stuck-deployment
///     reaper and the startup recovery of deployments orphaned by a control-plane restart.
/// </summary>
public sealed class DeploymentQueueJob(DeploymentQueueService queue, TimeProvider timeProvider)
{
    /// <summary>
    ///     A running deployment without any activity (log output or state change) for this long has no live worker
    ///     (Coolify: server deployment timeout of one hour plus a ten minute grace period).
    /// </summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(70);

    public Task AdvanceAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        return queue.AdvanceAsync(new ServerId(serverId), cancellationToken);
    }

    public Task<int> RecoverStuckAsync(CancellationToken cancellationToken = default)
    {
        return queue.RecoverAsync(timeProvider.GetUtcNow() - InactivityTimeout, null, cancellationToken);
    }

    /// <summary>Fails deployments that were running when the control plane stopped, then restarts the queues.</summary>
    public Task<int> RecoverAfterStartupAsync(DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        return queue.RecoverAsync(timeProvider.GetUtcNow() - InactivityTimeout, startedAt, cancellationToken);
    }
}

public static class DeploymentQueueRecurringJobs
{
    public const string RecoverJobId = "deployments.queue.recover";
    public const string RecoverCronExpression = "* * * * *";

    public static void Register(IRecurringJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        scheduler.AddOrUpdate<DeploymentQueueJob>(
            RecoverJobId,
            job => job.RecoverStuckAsync(CancellationToken.None),
            RecoverCronExpression);
    }

    /// <summary>Queues the one-off startup recovery of deployments orphaned by the previous control-plane process.</summary>
    public static void EnqueueStartupRecovery(IBackgroundJobDispatcher dispatcher, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Enqueue<DeploymentQueueJob>(job => job.RecoverAfterStartupAsync(startedAt, CancellationToken.None));
    }
}
