using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Deployments;

namespace Vessel.Application.Deployments;

public sealed class DeploymentQueryService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization)
{
    public DeploymentDetails Get(UserId actorUserId, TeamId teamId, DeploymentId deploymentId)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.DeploymentsReadLogs))
            throw new UnauthorizedAccessException($"Missing required permission '{VesselPermissions.DeploymentsReadLogs}'.");
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
            deployment.ArtifactReference,
            deployment.ConfigurationSnapshotReference,
            deployment.CreatedAt,
            deployment.StartedAt,
            deployment.FinishedAt,
            deployment.CancellationRequestedAt,
            deployment.LogLines
                .OrderBy(line => line.Sequence)
                .Select(line => new DeploymentLogEntry(line.Sequence, line.Stream, line.Message, line.CreatedAt))
                .ToArray());
    }
}
