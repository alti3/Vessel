using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Applications;
using Vessel.Domain.Auditing;
using Vessel.Domain.Servers;
using AppEntity = Vessel.Domain.Applications.Application;
using AppId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Deployments.Settings;

public sealed record ApplicationDeploymentSettingsDto(
    bool AutoDeployEnabled,
    bool PreviewDeploymentsEnabled,
    bool PublicPreviewsEnabled,
    bool ForceRebuild,
    string? WatchPaths,
    bool AutoCancelQueuedDeployments,
    bool AutoCancelBuildingDeployments,
    bool RedactAllEnvValuesInLogs,
    int ImagesToKeep);

/// <summary>Partial update: null fields keep their current value.</summary>
public sealed record UpdateApplicationDeploymentSettingsRequest(
    bool? AutoDeployEnabled = null,
    bool? PreviewDeploymentsEnabled = null,
    bool? PublicPreviewsEnabled = null,
    bool? ForceRebuild = null,
    string? WatchPaths = null,
    bool? AutoCancelQueuedDeployments = null,
    bool? AutoCancelBuildingDeployments = null,
    bool? RedactAllEnvValuesInLogs = null,
    int? ImagesToKeep = null);

public sealed record ServerDeploymentLimitsDto(int ConcurrentBuilds, int DeploymentQueueLimit);

/// <summary>
///     Deployment settings of applications (preview, public preview, auto-cancel, log redaction, image retention) and
///     deployment limits of servers. Changing them needs write permission on the resource. Enabling public previews
///     lets fork pull requests run code on the server (without production values), so every change is audited with
///     its before and after values.
/// </summary>
public sealed class DeploymentSettingsService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public ApplicationDeploymentSettingsDto Get(UserId actorUserId, TeamId teamId, AppId applicationId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsRead);
        return ToDto(FindApplication(applicationId).DeploymentSettings);
    }

    public async Task<ApplicationDeploymentSettingsDto> UpdateAsync(UserId actorUserId, TeamId teamId,
        AppId applicationId, UpdateApplicationDeploymentSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId),
            VesselPermissions.ApplicationsWrite);
        AppEntity application = FindApplication(applicationId);
        DeploymentSettings before = application.DeploymentSettings;
        DeploymentSettings after = before with
        {
            AutoDeployEnabled = request.AutoDeployEnabled ?? before.AutoDeployEnabled,
            PreviewDeploymentsEnabled = request.PreviewDeploymentsEnabled ?? before.PreviewDeploymentsEnabled,
            PublicPreviewsEnabled = request.PublicPreviewsEnabled ?? before.PublicPreviewsEnabled,
            ForceRebuild = request.ForceRebuild ?? before.ForceRebuild,
            WatchPaths = request.WatchPaths is null
                ? before.WatchPaths
                : string.IsNullOrWhiteSpace(request.WatchPaths) ? null : request.WatchPaths.Trim(),
            AutoCancelQueuedDeployments = request.AutoCancelQueuedDeployments ?? before.AutoCancelQueuedDeployments,
            AutoCancelBuildingDeployments =
            request.AutoCancelBuildingDeployments ?? before.AutoCancelBuildingDeployments,
            RedactAllEnvValuesInLogs = request.RedactAllEnvValuesInLogs ?? before.RedactAllEnvValuesInLogs,
            ImagesToKeep = request.ImagesToKeep ?? before.ImagesToKeep
        };

        application.UpdateDeploymentSettings(after, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ApplicationDeploymentSettingsUpdated,
            new AuditTarget("application", application.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["before"] = ToDto(before),
                ["after"] = ToDto(application.DeploymentSettings),
                ["publicPreviewsChanged"] = before.PublicPreviewsEnabled !=
                                            application.DeploymentSettings.PublicPreviewsEnabled
            }, cancellationToken);
        return ToDto(application.DeploymentSettings);
    }

    public ServerDeploymentLimitsDto GetServerLimits(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        Server server = FindServer(serverId);
        return new ServerDeploymentLimitsDto(server.ConcurrentBuilds, server.DeploymentQueueLimit);
    }

    public async Task<ServerDeploymentLimitsDto> UpdateServerLimitsAsync(UserId actorUserId, TeamId teamId,
        ServerId serverId, ServerDeploymentLimitsDto request, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = FindServer(serverId);
        var before = new ServerDeploymentLimitsDto(server.ConcurrentBuilds, server.DeploymentQueueLimit);
        server.UpdateDeploymentLimits(request.ConcurrentBuilds, request.DeploymentQueueLimit,
            timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        var after = new ServerDeploymentLimitsDto(server.ConcurrentBuilds, server.DeploymentQueueLimit);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ServerDeploymentLimitsUpdated,
            new AuditTarget("server", server.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["before"] = before, ["after"] = after }, cancellationToken);
        return after;
    }

    private AppEntity FindApplication(AppId applicationId)
    {
        return dbContext.Applications.SingleOrDefault(item => item.Id == applicationId)
               ?? throw new InvalidOperationException("Application was not found.");
    }

    private Server FindServer(ServerId serverId)
    {
        return dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
               ?? throw new InvalidOperationException("Server was not found.");
    }

    private static ApplicationDeploymentSettingsDto ToDto(DeploymentSettings settings)
    {
        return new ApplicationDeploymentSettingsDto(settings.AutoDeployEnabled, settings.PreviewDeploymentsEnabled,
            settings.PublicPreviewsEnabled, settings.ForceRebuild, settings.WatchPaths,
            settings.AutoCancelQueuedDeployments, settings.AutoCancelBuildingDeployments,
            settings.RedactAllEnvValuesInLogs, settings.ImagesToKeep);
    }
}
