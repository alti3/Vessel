using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Databases;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using AppEntity = Vessel.Domain.Applications.Application;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Resources;

/// <summary>A runtime resource after authorization: what it is, where it runs and which team owns it.</summary>
public sealed record AuthorizedRuntimeResource(
    ResourceRuntimeKind Kind,
    Guid Id,
    string Name,
    TeamId TeamId,
    EnvironmentId EnvironmentId,
    Server Server,
    AppEntity? Application = null,
    DatabaseResource? Database = null,
    ServiceResource? Service = null);

/// <summary>
///     Resolves an application, database or service for a runtime operation (status, logs) and demands a permission
///     on it. The owning team comes from the resource (applications and databases by their own reference, services
///     through their environment), never from the caller's active team, so cross-team access fails the same way as a
///     missing resource.
/// </summary>
public static class RuntimeResourceAccess
{
    public static AuthorizedRuntimeResource Demand(IVesselDbContext dbContext,
        VesselAuthorizationService authorization, UserId actorUserId, TeamId teamId, ResourceRuntimeKind kind,
        Guid resourceId, string permission)
    {
        switch (kind)
        {
            case ResourceRuntimeKind.Application:
            {
                var applicationId = new ApplicationId(resourceId);
                authorization.Demand(actorUserId, teamId, ResourceRef.Application(applicationId), permission);
                AppEntity application = dbContext.Applications.Single(item => item.Id == applicationId);
                return new AuthorizedRuntimeResource(kind, resourceId, application.Name.Value, teamId,
                    application.EnvironmentId, ServerOf(dbContext, application.ServerId), application);
            }
            case ResourceRuntimeKind.Database:
            {
                var databaseId = new DatabaseResourceId(resourceId);
                authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), permission);
                DatabaseResource database = dbContext.DatabaseResources.Single(item => item.Id == databaseId);
                return new AuthorizedRuntimeResource(kind, resourceId, database.Name.Value, teamId,
                    database.EnvironmentId, ServerOf(dbContext, database.ServerId), Database: database);
            }
            case ResourceRuntimeKind.Service:
            {
                var serviceId = new ServiceResourceId(resourceId);
                ServiceResource service = dbContext.ServiceResources.SingleOrDefault(item => item.Id == serviceId)
                                          ?? throw new UnauthorizedAccessException(
                                              "Service is outside the active team.");
                authorization.Demand(actorUserId, teamId, ResourceRef.Environment(service.EnvironmentId), permission);
                if (service.TeamId != teamId)
                    throw new UnauthorizedAccessException("Service is outside the active team.");
                return new AuthorizedRuntimeResource(kind, resourceId, service.Name.Value, teamId,
                    service.EnvironmentId, ServerOf(dbContext, service.ServerId), Service: service);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown resource kind.");
        }
    }

    private static Server ServerOf(IVesselDbContext dbContext, ServerId serverId)
    {
        return dbContext.Servers.SingleOrDefault(server => server.Id == serverId)
               ?? throw new InvalidOperationException("The resource's server was not found.");
    }
}
