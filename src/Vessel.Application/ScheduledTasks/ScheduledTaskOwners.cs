using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Databases;
using Vessel.Domain.ScheduledTasks;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using AppEntity = Vessel.Domain.Applications.Application;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.ScheduledTasks;

/// <summary>The resource that owns a task, with its server and environment.</summary>
public sealed record ScheduledTaskOwner(
    ScheduledTaskOwnerKind Kind,
    Guid Id,
    TeamId TeamId,
    string Name,
    ServerId ServerId,
    EnvironmentId EnvironmentId,
    ResourceRef AuthorizationTarget);

/// <summary>
///     Resolves and authorizes the owner of a scheduled task. Applications and databases use resource-scoped
///     authorization (the owning team is resolved from the resource, never trusted from the caller); services are
///     authorized on their environment and must belong to the active team. Unknown resources and other teams'
///     resources produce the same "outside the active team" failure.
/// </summary>
public sealed class ScheduledTaskOwners(IVesselDbContext dbContext, VesselAuthorizationService authorization)
{
    public ScheduledTaskOwner Demand(UserId actorUserId, TeamId teamId, ScheduledTaskOwnerKind kind, Guid ownerId,
        string permission)
    {
        ScheduledTaskOwner owner = Find(kind, ownerId) ??
                                   throw new UnauthorizedAccessException($"{kind} is outside the active team.");
        if (owner.TeamId != teamId) throw new UnauthorizedAccessException($"{kind} is outside the active team.");
        authorization.Demand(actorUserId, teamId, owner.AuthorizationTarget, permission);
        return owner;
    }

    public bool IsAuthorized(UserId actorUserId, TeamId teamId, ScheduledTaskOwner owner, string permission)
    {
        return owner.TeamId == teamId &&
               authorization.IsAuthorized(actorUserId, teamId, owner.AuthorizationTarget, permission);
    }

    /// <summary>Finds the owner without authorization (unattended runs).</summary>
    public ScheduledTaskOwner? Find(ScheduledTaskOwnerKind kind, Guid ownerId)
    {
        switch (kind)
        {
            case ScheduledTaskOwnerKind.Application:
                {
                    var id = new ApplicationId(ownerId);
                    AppEntity? application = dbContext.Applications.SingleOrDefault(item => item.Id == id);
                    if (application is null) return null;
                    TeamId? team = authorization.ResolveOwningTeam(ResourceRef.Application(id));
                    return team is null
                        ? null
                        : new ScheduledTaskOwner(kind, ownerId, team.Value, application.Name.Value,
                            application.ServerId, application.EnvironmentId, ResourceRef.Application(id));
                }
            case ScheduledTaskOwnerKind.Database:
                {
                    var id = new DatabaseResourceId(ownerId);
                    DatabaseResource? database = dbContext.DatabaseResources.SingleOrDefault(item => item.Id == id);
                    if (database is null) return null;
                    TeamId? team = authorization.ResolveOwningTeam(ResourceRef.Database(id));
                    return team is null
                        ? null
                        : new ScheduledTaskOwner(kind, ownerId, team.Value, database.Name.Value, database.ServerId,
                            database.EnvironmentId, ResourceRef.Database(id));
                }
            case ScheduledTaskOwnerKind.Service:
                {
                    var id = new ServiceResourceId(ownerId);
                    ServiceResource? service = dbContext.ServiceResources.SingleOrDefault(item => item.Id == id);
                    return service is null
                        ? null
                        : new ScheduledTaskOwner(kind, ownerId, service.TeamId, service.Name.Value, service.ServerId,
                            service.EnvironmentId, ResourceRef.Environment(service.EnvironmentId));
                }
            default:
                return null;
        }
    }

    public Server? FindServer(ScheduledTaskOwner owner)
    {
        return dbContext.Servers.SingleOrDefault(server => server.Id == owner.ServerId);
    }
}
