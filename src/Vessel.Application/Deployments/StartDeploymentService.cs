using System.Diagnostics;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Diagnostics;
using Vessel.Application.Jobs;
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
    IBackgroundJobDispatcher backgroundJobs,
    IAuditWriter auditWriter,
    IRealtimeNotifier realtime,
    TimeProvider timeProvider)
{
    public async Task<StartDeploymentResult> StartAsync(
        UserId actorUserId,
        TeamId teamId,
        StartDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = VesselDiagnostics.ActivitySource.StartActivity("StartDeployment");
        activity?.SetTag("vessel.application_id", request.ApplicationId);
        activity?.SetTag("vessel.team_id", teamId.Value);
        activity?.SetTag("vessel.force_rebuild", request.ForceRebuild);
        activity?.SetTag("vessel.webhook_event_id", request.WebhookEventId);

        var applicationId = new ApplicationId(request.ApplicationId);
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.DeploymentsStart))
            throw new UnauthorizedAccessException(
                $"Missing required permission '{VesselPermissions.DeploymentsStart}'.");
        if (!authorization.CanAccessApplication(actorUserId, applicationId))
            throw new UnauthorizedAccessException("Application is outside the active team.");

        Domain.Applications.Application application =
            dbContext.Applications.SingleOrDefault(application => application.Id == applicationId)
            ?? throw new InvalidOperationException("Application was not found.");
        Server server = dbContext.Servers.SingleOrDefault(server => server.Id == application.ServerId)
                        ?? throw new InvalidOperationException("Deployment server was not found.");
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Deployment server is unreachable.");

        var alreadyActive = dbContext.Deployments.Any(deployment =>
            deployment.ApplicationId == applicationId &&
            (deployment.Status == DeploymentStatus.Queued ||
             deployment.Status == DeploymentStatus.InProgress ||
             deployment.Status == DeploymentStatus.CancelRequested));
        if (alreadyActive)
            throw new DomainException("A deployment is already queued or running for this application.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        var deployment = Deployment.Queue(
            application.Id,
            application.ServerId,
            actorUserId,
            NormalizeCommit(request.CommitSha) ?? application.GitSource.CommitSha,
            request.PreviewId.HasValue ? new ApplicationPreviewId(request.PreviewId.Value) : null,
            request.WebhookEventId.HasValue ? new WebhookEventId(request.WebhookEventId.Value) : null,
            now);
        deployment.RecordSource(
            application.GitSource.RepositoryUrl.Value,
            application.GitSource.Branch,
            NormalizeCommit(request.CommitSha) ?? application.GitSource.CommitSha ?? "pending",
            null,
            now);
        deployment.AddLogLine("system",
            request.ForceRebuild ? "Deployment queued with force rebuild." : "Deployment queued.", now);

        await dbContext.DeploymentRepository.AddAsync(deployment, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        backgroundJobs.Enqueue<RunDeploymentJob>(job => job.RunAsync(deployment.Id.Value, CancellationToken.None));

        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DeploymentStarted,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["applicationId"] = application.Id.Value,
                ["serverId"] = application.ServerId.Value,
                ["forceRebuild"] = request.ForceRebuild,
                ["commitSha"] = NormalizeCommit(request.CommitSha),
                ["previewId"] = request.PreviewId,
                ["webhookEventId"] = request.WebhookEventId
            }, cancellationToken);

        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Deployment, deployment.Id.Value.ToString("D")),
            new RealtimeMessage("deployment.status",
                new { deploymentId = deployment.Id.Value, status = deployment.Status.ToString() }),
            cancellationToken);

        activity?.SetTag("vessel.deployment_id", deployment.Id.Value);
        VesselDiagnostics.DeploymentStartRequests.Add(1,
            new TagList
            {
                { "team_id", teamId.Value.ToString("D") },
                { "force_rebuild", request.ForceRebuild }
            });

        return new StartDeploymentResult(deployment.Id.Value, application.Id.Value, deployment.Status,
            "Deployment accepted.");
    }

    private static string? NormalizeCommit(string? commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha)) return null;
        var trimmed = commitSha.Trim();
        if (trimmed.Length > 80) throw new DomainException("Commit reference is too long.");
        return trimmed;
    }

    public async Task CancelAsync(
        UserId actorUserId,
        TeamId teamId,
        DeploymentId deploymentId,
        CancellationToken cancellationToken = default)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.DeploymentsCancel))
            throw new UnauthorizedAccessException(
                $"Missing required permission '{VesselPermissions.DeploymentsCancel}'.");
        if (!authorization.CanAccessDeployment(actorUserId, deploymentId))
            throw new UnauthorizedAccessException("Deployment is outside the active team.");

        Deployment deployment = await dbContext.DeploymentRepository.GetByIdAsync(deploymentId, cancellationToken)
                                ?? throw new InvalidOperationException("Deployment was not found.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        deployment.RequestCancellation(now);
        deployment.AddLogLine("system", "Deployment cancellation requested by user.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.DeploymentCanceled,
            new AuditTarget("deployment", deployment.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>(), cancellationToken);

        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Deployment, deployment.Id.Value.ToString("D")),
            new RealtimeMessage("deployment.status",
                new { deploymentId = deployment.Id.Value, status = deployment.Status.ToString() }),
            cancellationToken);
    }
}
