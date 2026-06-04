using Vessel.Application.Auditing;
using Vessel.Application.Persistence;
using Vessel.Domain.Auditing;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments;

public sealed class DeploymentLogRetentionService(
    IVesselDbContext dbContext,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<DeploymentLogRetentionResult> PruneAsync(
        DeploymentLogRetentionOptions options,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow().Subtract(options.RetentionPeriod);
        if (options.BatchSize == 0) return new DeploymentLogRetentionResult(0, cutoff);

        int deleted = 0;

        Deployment[] candidates = dbContext.Deployments
            .Where(deployment => deployment.FinishedAt.HasValue && deployment.FinishedAt.Value < cutoff)
            .OrderBy(deployment => deployment.FinishedAt)
            .Take(options.BatchSize)
            .ToArray();

        foreach (Deployment deployment in candidates)
        {
            int before = deployment.LogLines.Count;
            deployment.PruneLogLines(cutoff);
            deleted += before - deployment.LogLines.Count;
        }

        if (deleted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(
                null,
                null,
                AuditActions.DeploymentLogsPruned,
                new AuditTarget("system", "deployment_logs"),
                null,
                new Dictionary<string, object?>
                {
                    ["cutoff"] = cutoff,
                    ["deletedLineCount"] = deleted
                },
                cancellationToken);
        }

        return new DeploymentLogRetentionResult(deleted, cutoff);
    }
}
