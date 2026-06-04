using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments;

public sealed class DeploymentQueryService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ISecretRedactor redactor)
{
    private const int MaxPageSize = 1000;

    public DeploymentDetails Get(UserId actorUserId, TeamId teamId, DeploymentId deploymentId)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.DeploymentsReadLogs))
            throw new UnauthorizedAccessException(
                $"Missing required permission '{VesselPermissions.DeploymentsReadLogs}'.");
        if (!authorization.CanAccessDeployment(actorUserId, deploymentId))
            throw new UnauthorizedAccessException("Deployment is outside the active team.");

        Deployment deployment = dbContext.Deployments.SingleOrDefault(deployment => deployment.Id == deploymentId)
                                ?? throw new InvalidOperationException("Deployment was not found.");

        return new DeploymentDetails(
            deployment.Id.Value,
            deployment.ApplicationId.Value,
            deployment.ServerId.Value,
            deployment.Status,
            deployment.RepositoryUrl,
            deployment.CommitBranch,
            deployment.CommitSha,
            deployment.CommitMessage,
            deployment.IsPreview,
            deployment.IsWebhookTriggered,
            deployment.ArtifactReference,
            deployment.ConfigurationSnapshotReference,
            deployment.CreatedAt,
            deployment.StartedAt,
            deployment.FinishedAt,
            deployment.CancellationRequestedAt,
            GetLogs(actorUserId, teamId, deploymentId, new DeploymentLogQuery(PageSize: 200)).Entries);
    }

    public DeploymentLogPage GetLogs(
        UserId actorUserId,
        TeamId teamId,
        DeploymentId deploymentId,
        DeploymentLogQuery query)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.DeploymentsReadLogs))
            throw new UnauthorizedAccessException(
                $"Missing required permission '{VesselPermissions.DeploymentsReadLogs}'.");
        if (!authorization.CanAccessDeployment(actorUserId, deploymentId))
            throw new UnauthorizedAccessException("Deployment is outside the active team.");

        Deployment deployment = dbContext.Deployments.SingleOrDefault(deployment => deployment.Id == deploymentId)
                                ?? throw new InvalidOperationException("Deployment was not found.");

        int pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        IEnumerable<DeploymentLogLine> lines = deployment.LogLines;

        if (query.AfterSequence.HasValue)
            lines = lines.Where(line => query.Descending
                ? line.Sequence < query.AfterSequence.Value
                : line.Sequence > query.AfterSequence.Value);

        if (!string.IsNullOrWhiteSpace(query.Stream))
            lines = lines.Where(line => string.Equals(line.Stream, query.Stream, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = redactor.Redact(query.Search.Trim());
            lines = lines.Where(line => line.Message.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        lines = query.Descending
            ? lines.OrderByDescending(line => line.Sequence)
            : lines.OrderBy(line => line.Sequence);

        DeploymentLogEntry[] entries = lines
            .Take(pageSize + 1)
            .Select(line => new DeploymentLogEntry(line.Sequence, line.Stream, redactor.Redact(line.Message),
                line.CreatedAt))
            .ToArray();

        bool hasMore = entries.Length > pageSize;
        DeploymentLogEntry[] pageEntries = entries.Take(pageSize).ToArray();

        return new DeploymentLogPage(
            deployment.Id.Value,
            pageEntries,
            hasMore && pageEntries.Length > 0 ? pageEntries[^1].Sequence : null,
            hasMore,
            deployment.Status);
    }
}
