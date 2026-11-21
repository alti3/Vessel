using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Monitoring;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Monitoring;

namespace Vessel.Application.Resources.Status;

public sealed record ResourceRuntimeStatusDto(
    string Kind,
    Guid ResourceId,
    Guid ServerId,
    string State,
    string Health,
    int ContainerCount,
    int RunningContainerCount,
    int RestartCount,
    int MaxRestartCount,
    bool RestartLimitReached,
    DateTimeOffset? LastRestartAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSeenRunningAt);

public sealed record UpdateRestartLimitRequest(int MaxRestartCount);

/// <summary>Runtime status queries and the opt-in restart limit of applications, databases and services (SRV-04).</summary>
public sealed class ResourceRuntimeStatusService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public ResourceRuntimeStatusDto Get(UserId actorUserId, TeamId teamId, ResourceRuntimeKind kind, Guid resourceId)
    {
        var permission = kind == ResourceRuntimeKind.Application
            ? VesselPermissions.ApplicationsRead
            : VesselPermissions.ProjectsRead;
        AuthorizedRuntimeResource resource = RuntimeResourceAccess.Demand(dbContext, authorization, actorUserId,
            teamId, kind, resourceId, permission);
        ResourceRuntimeStatus? status = Find(kind, resourceId);
        return status is null
            ? new ResourceRuntimeStatusDto(kind.ToString(), resourceId, resource.Server.Id.Value,
                ResourceRuntimeState.Unknown.ToString(), ResourceHealth.Unknown.ToString(), 0, 0, 0, 0, false, null,
                timeProvider.GetUtcNow(), null, null)
            : ToDto(status);
    }

    /// <summary>Sets the restart limit (0 disables it). Deploy-level permission, like start/stop.</summary>
    public async Task<ResourceRuntimeStatusDto> ConfigureRestartLimitAsync(UserId actorUserId, TeamId teamId,
        ResourceRuntimeKind kind, Guid resourceId, UpdateRestartLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AuthorizedRuntimeResource resource = RuntimeResourceAccess.Demand(dbContext, authorization, actorUserId,
            teamId, kind, resourceId, VesselPermissions.ResourcesLifecycle);
        ResourceRuntimeStatus status = await GetOrCreateAsync(resource, cancellationToken);
        status.ConfigureRestartLimit(request.MaxRestartCount, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.ResourceRestartLimitUpdated,
            new AuditTarget(kind.ToString().ToLowerInvariant(), resourceId.ToString("D")), null,
            new Dictionary<string, object?> { ["maxRestartCount"] = request.MaxRestartCount }, cancellationToken);
        return ToDto(status);
    }

    /// <summary>Clears a reached restart limit so the resource can be started or redeployed (upstream retry).</summary>
    public async Task<ResourceRuntimeStatusDto> ResetRestartLimitAsync(UserId actorUserId, TeamId teamId,
        ResourceRuntimeKind kind, Guid resourceId, CancellationToken cancellationToken = default)
    {
        AuthorizedRuntimeResource resource = RuntimeResourceAccess.Demand(dbContext, authorization, actorUserId,
            teamId, kind, resourceId, VesselPermissions.ResourcesLifecycle);
        ResourceRuntimeStatus status = await GetOrCreateAsync(resource, cancellationToken);
        status.ResetRestartLimit(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.ResourceRestartLimitReset,
            new AuditTarget(kind.ToString().ToLowerInvariant(), resourceId.ToString("D")), null,
            new Dictionary<string, object?>(), cancellationToken);
        return ToDto(status);
    }

    public static ResourceRuntimeStatusDto ToDto(ResourceRuntimeStatus status)
    {
        return new ResourceRuntimeStatusDto(status.Kind.ToString(), status.ResourceId, status.ServerId.Value,
            status.State.ToString(), status.Health.ToString(), status.ContainerCount, status.RunningContainerCount,
            status.RestartCount, status.MaxRestartCount, status.RestartLimitReached, status.LastRestartAt,
            status.StateChangedAt, status.LastCheckedAt, status.LastSeenRunningAt);
    }

    private ResourceRuntimeStatus? Find(ResourceRuntimeKind kind, Guid resourceId)
    {
        return dbContext.ResourceRuntimeStatuses.SingleOrDefault(item =>
            item.Kind == kind && item.ResourceId == resourceId);
    }

    private async Task<ResourceRuntimeStatus> GetOrCreateAsync(AuthorizedRuntimeResource resource,
        CancellationToken cancellationToken)
    {
        ResourceRuntimeStatus? status = Find(resource.Kind, resource.Id);
        if (status is not null) return status;
        status = ResourceRuntimeStatus.Create(resource.Kind, resource.Id, resource.TeamId, resource.Server.Id,
            timeProvider.GetUtcNow());
        await dbContext.ResourceRuntimeStatusRepository.AddAsync(status, cancellationToken);
        return status;
    }
}
