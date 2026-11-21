using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Docker;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Deployments.Rollback;

/// <summary>A rollback target: a listed image and/or the successful production deployment that produced it.</summary>
public sealed record RollbackTarget(
    string Image,
    string Tag,
    string? CommitSha,
    Guid? DeploymentId,
    DateTimeOffset? DeployedAt,
    bool IsLocal,
    bool IsCurrent);

public sealed record RollbackRequest(Guid? DeploymentId = null, string? Image = null, string? CommitSha = null);

/// <summary>
///     Rollback (DEP-06, Coolify <c>GetRollbackImages</c> + <c>allows()</c>). Targets are local images of the
///     application's own image repository (newest first) plus the images of the most recent successful production
///     deployments. A rollback is accepted only to a listed image or to the commit of a successful, non-preview,
///     non-rollback deployment: when the image is gone the commit is rebuilt, so any other commit is refused.
///     Rollbacks are never auto-cancelled. Registry images are not listed: Vessel builds and keeps images locally.
/// </summary>
public sealed class RollbackService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IContainerRuntimeClient runtime,
    DeploymentQueueService queue,
    IAuditWriter auditWriter)
{
    /// <summary>How many successful production deployments are offered (Coolify lists the last 10).</summary>
    public const int MaxDeploymentTargets = 10;

    public async Task<IReadOnlyList<RollbackTarget>> ListTargetsAsync(UserId actorUserId, TeamId teamId,
        AppId applicationId, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsRead);
        (AppEntity application, Server server) = Context(applicationId);
        // Listing local images probes the runtime: on a host-local server only an instance admin may do that.
        var probeRuntime = !server.IsHostLocal || authorization.IsInstanceAdmin(actorUserId);
        return await ListAsync(application, server, probeRuntime, cancellationToken);
    }

    public async Task<StartDeploymentResult> RollbackAsync(UserId actorUserId, TeamId teamId, AppId applicationId,
        RollbackRequest request, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.DeploymentsStart);
        (AppEntity application, Server server) = Context(applicationId);
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Deployment server is unreachable.");
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);

        IReadOnlyList<RollbackTarget> targets = await ListAsync(application, server, true, cancellationToken);
        RollbackTarget target = Resolve(targets, request);
        if (target.CommitSha is null || !GitRef.IsCommitShaValue(target.CommitSha))
            throw new DomainException("The rollback target has no commit to rebuild from.");

        DeploymentAdmission admission = await queue.AdmitAsync(new DeploymentAdmissionRequest(
            application,
            actorUserId,
            DeploymentTrigger.Rollback,
            target.CommitSha,
            Rollback: new DeploymentRollbackTarget(target.CommitSha,
                target.DeploymentId.HasValue ? new DeploymentId(target.DeploymentId.Value) : null,
                target.Image),
            QueuedMessage: $"Rollback to {target.Image} queued."), cancellationToken);
        Deployment deployment = admission.Deployment;

        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DeploymentRollbackQueued,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["applicationId"] = application.Id.Value,
                ["image"] = target.Image,
                ["commitSha"] = target.CommitSha,
                ["targetDeploymentId"] = target.DeploymentId
            }, cancellationToken);
        return new StartDeploymentResult(deployment.Id.Value, application.Id.Value, deployment.Status,
            "Rollback queued.");
    }

    /// <summary>
    ///     Accepts only a listed image, a listed deployment, or the commit of a listed (successful production)
    ///     deployment.
    /// </summary>
    public static RollbackTarget Resolve(IReadOnlyList<RollbackTarget> targets, RollbackRequest request)
    {
        RollbackTarget? match = null;
        if (request.DeploymentId.HasValue)
            match = targets.FirstOrDefault(target => target.DeploymentId == request.DeploymentId);
        else if (!string.IsNullOrWhiteSpace(request.Image))
            match = targets.FirstOrDefault(target =>
                string.Equals(target.Image, request.Image.Trim(), StringComparison.Ordinal) ||
                string.Equals(target.Tag, request.Image.Trim(), StringComparison.Ordinal));
        else if (!string.IsNullOrWhiteSpace(request.CommitSha))
            match = targets.FirstOrDefault(target => target.DeploymentId.HasValue &&
                                                     string.Equals(target.CommitSha, request.CommitSha.Trim(),
                                                         StringComparison.OrdinalIgnoreCase));
        return match ?? throw new DomainException(
            "Rollback is allowed only to a listed image or to a successful production deployment.");
    }

    private async Task<IReadOnlyList<RollbackTarget>> ListAsync(AppEntity application, Server server,
        bool probeRuntime, CancellationToken cancellationToken)
    {
        var repository = DeploymentPlanBuilder.ImageRepository(application.Id) + ":";
        AppId applicationId = application.Id;
        Deployment[] succeeded = dbContext.Deployments
            .Where(item => item.ApplicationId == applicationId && item.PreviewId == null && !item.IsRollback &&
                           item.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(item => item.FinishedAt)
            .Take(MaxDeploymentTargets * 3)
            .ToArray();
        Deployment? current = dbContext.Deployments
            .Where(item => item.ApplicationId == applicationId && item.PreviewId == null &&
                           item.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(item => item.FinishedAt)
            .FirstOrDefault();

        HashSet<string> local = [];
        try
        {
            IReadOnlyList<ImageSummary> images = !probeRuntime
                ? []
                : await runtime.ListImagesAsync(Target(server), cancellationToken);
            local = images.SelectMany(image => image.RepoTags)
                .Where(tag => tag.StartsWith(repository, StringComparison.Ordinal) &&
                              !tag[repository.Length..].StartsWith("pr-", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreachable runtime still lists deployment targets (they rebuild from their commit).
        }

        var targets = new List<RollbackTarget>();
        foreach (Deployment deployment in succeeded)
        {
            var image = deployment.ArtifactReference ??
                        DeploymentPlanBuilder.ProductionImage(application.Id, deployment.CommitSha, deployment.Id);
            if (targets.Any(target => target.Image == image)) continue;
            targets.Add(new RollbackTarget(image, image[(image.LastIndexOf(':') + 1)..], deployment.CommitSha,
                deployment.Id.Value, deployment.FinishedAt, local.Contains(image),
                current?.ArtifactReference == image));
            if (targets.Count >= MaxDeploymentTargets) break;
        }

        // Local images of the application that no listed deployment produced (for example after a history prune);
        // their tag is the commit they were built from.
        foreach (var image in local.Where(image => targets.All(target => target.Image != image)).Order())
        {
            var tag = image[repository.Length..];
            targets.Add(new RollbackTarget(image, tag, GitRef.IsCommitShaValue(tag) ? tag : null, null, null, true,
                current?.ArtifactReference == image));
        }

        return targets;
    }

    private (AppEntity Application, Server Server) Context(AppId applicationId)
    {
        AppEntity application = dbContext.Applications.SingleOrDefault(item => item.Id == applicationId)
                                ?? throw new InvalidOperationException("Application was not found.");
        Server server = dbContext.Servers.Single(item => item.Id == application.ServerId);
        return (application, server);
    }

    private static ContainerRuntimeTarget Target(Server server)
    {
        return new ContainerRuntimeTarget(server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker);
    }
}
