using System.Diagnostics;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Deployments.Queue;
using Vessel.Application.Diagnostics;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Deployments;
using Vessel.Domain.Servers;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Deployments;

public sealed class StartDeploymentService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    DeploymentQueueService queue,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public Task<StartDeploymentResult> StartAsync(
        UserId actorUserId,
        TeamId teamId,
        StartDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(actorUserId, teamId, request, DeploymentTrigger.Manual, cancellationToken);
    }

    /// <summary>
    ///     Queues a deployment (DEP-04): a request while another deployment runs is queued, not rejected; an identical
    ///     active request returns the existing deployment. <paramref name="trigger" /> is decided by the caller (UI:
    ///     manual, REST API: api, preview API: preview), never by the request body.
    /// </summary>
    public async Task<StartDeploymentResult> StartAsync(
        UserId actorUserId,
        TeamId teamId,
        StartDeploymentRequest request,
        DeploymentTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        if (trigger is DeploymentTrigger.Webhook or DeploymentTrigger.Rollback)
            throw new DomainException("Webhook and rollback deployments are queued by their own services.");
        using Activity? activity = VesselDiagnostics.ActivitySource.StartActivity("StartDeployment");
        activity?.SetTag("vessel.application_id", request.ApplicationId);
        activity?.SetTag("vessel.team_id", teamId.Value);
        activity?.SetTag("vessel.force_rebuild", request.ForceRebuild);
        activity?.SetTag("vessel.deployment_trigger", trigger.ToString());

        var applicationId = new ApplicationId(request.ApplicationId);
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.DeploymentsStart);

        Domain.Applications.Application application =
            dbContext.Applications.SingleOrDefault(application => application.Id == applicationId)
            ?? throw new InvalidOperationException("Application was not found.");
        Server server = dbContext.Servers.SingleOrDefault(server => server.Id == application.ServerId)
                        ?? throw new InvalidOperationException("Deployment server was not found.");
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Deployment server is unreachable.");
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);

        // Refs are validated before queueing and again when the deployment starts.
        var requestedRef = NormalizeCommit(request.CommitSha);
        ApplicationPreviewId? previewId = null;
        if (request.PreviewId.HasValue)
        {
            var requestedPreview = new ApplicationPreviewId(request.PreviewId.Value);
            Domain.Webhooks.ApplicationPreview preview =
                dbContext.ApplicationPreviews.SingleOrDefault(item => item.Id == requestedPreview)
                ?? throw new InvalidOperationException("Preview was not found.");
            if (preview.ApplicationId != applicationId)
                throw new UnauthorizedAccessException("Preview does not belong to the application.");
            if (preview.Status == Domain.Webhooks.ApplicationPreviewStatus.Archived)
                throw new DomainException("Preview is closed and cannot be deployed.");
            // A stopped preview is started again by a redeploy.
            if (preview.Status == Domain.Webhooks.ApplicationPreviewStatus.Stopped)
            {
                preview.Reopen(timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            previewId = preview.Id;
        }

        // Only API callers may ask to cancel older deployments (Coolify `cancel_previous`, default none).
        CancelPreviousDeployments cancelPrevious = SupersededDeploymentPolicy.Parse(request.CancelPrevious);
        if (trigger != DeploymentTrigger.Api && cancelPrevious != CancelPreviousDeployments.None)
            throw new DomainException("cancel_previous is only supported for API deployments.");

        DeploymentId? parentId = null;
        if (request.ParentDeploymentId.HasValue)
        {
            var requestedParent = new DeploymentId(request.ParentDeploymentId.Value);
            Deployment parent = dbContext.Deployments.SingleOrDefault(item => item.Id == requestedParent)
                                ?? throw new InvalidOperationException("Parent deployment was not found.");
            if (parent.ApplicationId != applicationId)
                throw new UnauthorizedAccessException("Parent deployment does not belong to the application.");
            parentId = parent.Id;
        }

        DeploymentAdmission admission = await queue.AdmitAsync(new DeploymentAdmissionRequest(
            application,
            actorUserId,
            trigger,
            requestedRef,
            previewId,
            null,
            request.ForceRebuild,
            cancelPrevious,
            parentId,
            null,
            request.ForceRebuild ? "Deployment queued with force rebuild." : "Deployment queued."), cancellationToken);
        Deployment deployment = admission.Deployment;

        if (admission.Outcome == DeploymentAdmissionOutcome.Deduplicated)
            return new StartDeploymentResult(deployment.Id.Value, application.Id.Value, deployment.Status,
                "Deployment already queued for this commit.", true);

        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DeploymentStarted,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["applicationId"] = application.Id.Value,
                ["serverId"] = application.ServerId.Value,
                ["forceRebuild"] = request.ForceRebuild,
                ["commitSha"] = requestedRef,
                ["previewId"] = request.PreviewId,
                ["trigger"] = trigger.ToString(),
                ["parentDeploymentId"] = parentId?.Value,
                ["cancelPrevious"] = cancelPrevious.ToString(),
                ["supersededDeployments"] = admission.Superseded.Select(id => id.Value).ToArray()
            }, cancellationToken);

        activity?.SetTag("vessel.deployment_id", deployment.Id.Value);
        VesselDiagnostics.DeploymentStartRequests.Add(1,
            new TagList
            {
                { "team_id", teamId.Value.ToString("D") },
                { "force_rebuild", request.ForceRebuild }
            });

        return new StartDeploymentResult(deployment.Id.Value, application.Id.Value, deployment.Status,
            admission.Outcome == DeploymentAdmissionOutcome.Dispatched
                ? "Deployment accepted."
                : "Deployment queued behind other deployments.");
    }

    private static string? NormalizeCommit(string? commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha)) return null;
        var trimmed = commitSha.Trim();
        if (trimmed.Length > 80) throw new DomainException("Commit reference is too long.");
        return DeploymentPlanBuilder.NormalizeRequestedRef(trimmed);
    }

    public async Task CancelAsync(
        UserId actorUserId,
        TeamId teamId,
        DeploymentId deploymentId,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Deployment(deploymentId),
            VesselPermissions.DeploymentsCancel);

        Deployment deployment = await dbContext.DeploymentRepository.GetByIdAsync(deploymentId, cancellationToken)
                                ?? throw new InvalidOperationException("Deployment was not found.");
        await queue.RequestCancellationAsync(deployment, "Deployment cancellation requested by user.",
            cancellationToken);

        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DeploymentCanceled,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>(), cancellationToken);
    }
}
