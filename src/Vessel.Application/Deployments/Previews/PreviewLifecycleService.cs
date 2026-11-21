using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Webhooks;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Servers;
using Vessel.Domain.Webhooks;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Deployments.Previews;

/// <summary>
///     Preview lifecycle (DEP-07, Coolify <c>StopApplicationPreview</c>, <c>ApplicationPreviewsController</c>, closed-PR
///     cleanup): list, redeploy, stop and close previews. Cleanup removes only what belongs to the preview: its compose
///     project (containers, project networks and named volumes), its images (exact <c>pr-{n}-</c> tag prefix, so PR 1
///     never matches PR 12) and its proxy route. Every step is idempotent, so the cleanup job is retry-safe.
/// </summary>
public sealed class PreviewLifecycleService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IContainerRuntimeClient runtime,
    ProxyConfigurationService proxyConfiguration,
    StartDeploymentService deployments,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public IReadOnlyList<ApplicationPreviewSummary> List(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsRead);
        return dbContext.ApplicationPreviews
            .Where(preview => preview.ApplicationId == applicationId)
            .AsEnumerable()
            .OrderByDescending(preview => preview.PullRequestNumber)
            .Select(ToSummary)
            .ToArray();
    }

    public ApplicationPreviewSummary Get(UserId actorUserId, TeamId teamId, AppId applicationId,
        int pullRequestNumber)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsRead);
        return ToSummary(Find(applicationId, pullRequestNumber));
    }

    /// <summary>Redeploys an existing preview (a stopped preview starts again). Needs deploy permission only.</summary>
    public async Task<StartDeploymentResult> RedeployAsync(UserId actorUserId, TeamId teamId, AppId applicationId,
        int pullRequestNumber, bool forceRebuild, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.DeploymentsStart);
        ApplicationPreview preview = Find(applicationId, pullRequestNumber);
        return await deployments.StartAsync(actorUserId, teamId,
            new StartDeploymentRequest(applicationId.Value, forceRebuild, null, preview.Id.Value),
            DeploymentTrigger.Preview, cancellationToken);
    }

    /// <summary>Stops a preview: its containers are removed, its volumes kept, its route dropped.</summary>
    public async Task<ApplicationPreviewSummary> StopAsync(UserId actorUserId, TeamId teamId, AppId applicationId,
        int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.DeploymentsStart);
        ApplicationPreview preview = Find(applicationId, pullRequestNumber);
        (AppEntity application, Server server) = Context(preview);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        RequireNoActiveDeployment(preview);

        if (!await runtime.RemoveComposeProjectAsync(Target(server), ProjectName(application, server, preview), false,
                cancellationToken))
            throw new DomainException("The container runtime could not stop the preview.");
        preview.Stop(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await ReapplyRoutesAsync(teamId, server, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.PreviewStopped,
            new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
                { ["applicationId"] = application.Id.Value, ["pullRequest"] = preview.PullRequestNumber },
            cancellationToken);
        return ToSummary(preview);
    }

    /// <summary>Closes a preview (as if its pull request closed) and removes all of its runtime resources.</summary>
    public async Task<ApplicationPreviewSummary> CloseAsync(UserId actorUserId, TeamId teamId, AppId applicationId,
        int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsWrite);
        ApplicationPreview preview = Find(applicationId, pullRequestNumber);
        (_, Server server) = Context(preview);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        RequireNoActiveDeployment(preview);

        if (preview.Status != ApplicationPreviewStatus.Archived)
        {
            preview.Archive(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.PreviewArchived,
                new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                    { ["applicationId"] = applicationId.Value, ["pullRequest"] = preview.PullRequestNumber },
                cancellationToken);
        }

        await CleanupAsync(preview.Id, actorUserId, cancellationToken);
        return ToSummary(preview);
    }

    /// <summary>
    ///     Removes the runtime resources of an archived preview. Does nothing when the preview is open again (its pull
    ///     request was reopened). Throws when the runtime fails so the job is retried.
    /// </summary>
    public async Task CleanupAsync(ApplicationPreviewId previewId, UserId? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ApplicationPreview? preview = dbContext.ApplicationPreviews.SingleOrDefault(item => item.Id == previewId);
        if (preview is null) return;
        await dbContext.RefreshAsync(preview, cancellationToken);
        if (preview.Status != ApplicationPreviewStatus.Archived) return;

        (AppEntity application, Server server) = Context(preview);
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Preview cleanup waits until the server is reachable again.");
        // Teardown of Vessel-owned preview resources only; it never runs repository code.
        ContainerRuntimeTarget target = Target(server);
        if (!await runtime.RemoveComposeProjectAsync(target, ProjectName(application, server, preview), true,
                cancellationToken))
            throw new DomainException("The container runtime could not remove the preview containers.");

        var imagePrefix = $"{DeploymentPlanBuilder.ImageRepository(application.Id)}:" +
                          $"{DeploymentPlanBuilder.PreviewSuffix(preview.PullRequestNumber)}-";
        IReadOnlyList<ImageSummary> images = await runtime.ListImagesAsync(target, cancellationToken);
        foreach (var image in images.SelectMany(item => item.RepoTags)
                     .Where(tag => tag.StartsWith(imagePrefix, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal))
            await runtime.RemoveImageAsync(target, image, cancellationToken);

        if (preview.PreviewUrl is not null)
        {
            preview.SetPreviewUrl(null, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        TeamId teamId = WebhookApplicationMatching.TeamForApplication(dbContext, application);
        await ReapplyRoutesAsync(teamId, server, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.PreviewCleanedUp,
            new AuditTarget("preview", preview.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
                { ["applicationId"] = application.Id.Value, ["pullRequest"] = preview.PullRequestNumber },
            cancellationToken);
    }

    public static string ProjectName(AppEntity application, Server server, ApplicationPreview preview)
    {
        return DeploymentPlanBuilder.Naming(application.Id, server.Id, new DeploymentId(Guid.Empty), preview)
            .ProjectName;
    }

    private async Task ReapplyRoutesAsync(TeamId teamId, Server server, CancellationToken cancellationToken)
    {
        // Routes are generated from open previews only, so a re-apply drops this preview's route.
        await proxyConfiguration.ApplyForDeploymentAsync(null, teamId, server.Id, cancellationToken);
    }

    private void RequireNoActiveDeployment(ApplicationPreview preview)
    {
        ApplicationPreviewId previewId = preview.Id;
        if (dbContext.Deployments.Any(item => item.PreviewId == previewId &&
                                              (item.Status == DeploymentStatus.Queued ||
                                               item.Status == DeploymentStatus.InProgress ||
                                               item.Status == DeploymentStatus.CancelRequested)))
            throw new DomainException("A deployment of this preview is queued or running; cancel it first.");
    }

    private ApplicationPreview Find(AppId applicationId, int pullRequestNumber)
    {
        return dbContext.ApplicationPreviews
                   .Where(item => item.ApplicationId == applicationId && item.PullRequestNumber == pullRequestNumber)
                   .AsEnumerable()
                   .OrderByDescending(item => item.Status == ApplicationPreviewStatus.Open)
                   .ThenByDescending(item => item.UpdatedAt)
                   .FirstOrDefault()
               ?? throw new InvalidOperationException("Preview was not found.");
    }

    private (AppEntity Application, Server Server) Context(ApplicationPreview preview)
    {
        AppEntity application = dbContext.Applications.Single(item => item.Id == preview.ApplicationId);
        Server server = dbContext.Servers.Single(item => item.Id == application.ServerId);
        return (application, server);
    }

    private static ContainerRuntimeTarget Target(Server server)
    {
        // The server's own runtime: local for host-local servers, the remote CLI over SSH for SSH servers.
        return ContainerRuntimeTarget.For(server);
    }

    public static ApplicationPreviewSummary ToSummary(ApplicationPreview preview)
    {
        return new ApplicationPreviewSummary(preview.Id.Value, preview.ApplicationId.Value, preview.Provider,
            preview.PullRequestNumber, preview.SourceBranch, preview.TargetBranch, preview.CommitSha,
            preview.PullRequestUrl, preview.Title, preview.PreviewUrl, preview.Status.ToString(),
            preview.IsSameRepository, preview.SourceRepository, preview.ClosedAt);
    }
}

/// <summary>Thin Hangfire job: retry-safe runtime cleanup of a closed preview.</summary>
public sealed class CleanupPreviewJob(PreviewLifecycleService previews)
{
    public Task RunAsync(Guid previewId, CancellationToken cancellationToken = default)
    {
        return previews.CleanupAsync(new ApplicationPreviewId(previewId), null, cancellationToken);
    }
}
