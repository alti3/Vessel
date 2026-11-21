using Vessel.Application.Dashboard;
using Vessel.Application.Persistence;
using Vessel.Domain;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Authorization;

/// <summary>
///     Team catalog lists filtered by the actor's access (IAM-05): scoped members and tokens only see the
///     applications, databases, deployments and servers their grants reach. Pages and controllers use this instead of
///     the raw <c>*CatalogQuery</c> interfaces, which list everything in a team.
/// </summary>
public sealed class AccessScopedCatalog(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IApplicationCatalogQuery applications,
    IDatabaseCatalogQuery databases,
    IDeploymentCatalogQuery deployments,
    IServerCatalogQuery servers)
{
    public IReadOnlyList<ApplicationListItem> ListApplications(UserId actorUserId, TeamId teamId)
    {
        if (!authorization.HasPermissionSomewhere(actorUserId, teamId, VesselPermissions.ApplicationsRead)) return [];
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);
        return applications.List(teamId)
            .Where(item => visibility.CanSeeResource(item.EnvironmentId, item.Id))
            .ToArray();
    }

    public IReadOnlyList<DatabaseListItem> ListDatabases(UserId actorUserId, TeamId teamId)
    {
        if (!authorization.HasPermissionSomewhere(actorUserId, teamId, VesselPermissions.ProjectsRead)) return [];
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);
        return databases.List(teamId)
            .Where(item => visibility.CanSeeResource(item.EnvironmentId, item.Id))
            .ToArray();
    }

    public IReadOnlyList<DeploymentListItem> ListDeployments(UserId actorUserId, TeamId teamId)
    {
        if (!authorization.HasPermissionSomewhere(actorUserId, teamId, VesselPermissions.DeploymentsReadLogs))
            return [];
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);
        IReadOnlyList<DeploymentListItem> items = deployments.List(teamId);
        if (visibility.IsUnrestricted) return items;

        ApplicationId[] applicationIds = items.Select(item => new ApplicationId(item.ApplicationId)).Distinct()
            .ToArray();
        var environments = dbContext.Applications
            .Where(application => applicationIds.Contains(application.Id))
            .Select(application => new { application.Id, application.EnvironmentId })
            .ToArray()
            .ToDictionary(application => application.Id.Value, application => application.EnvironmentId.Value);

        return items
            .Where(item => environments.TryGetValue(item.ApplicationId, out Guid environmentId)
                           && visibility.CanSeeResource(environmentId, item.ApplicationId))
            .ToArray();
    }

    public IReadOnlyList<ServerListItem> ListServers(UserId actorUserId, TeamId teamId)
    {
        if (!authorization.HasPermissionSomewhere(actorUserId, teamId, VesselPermissions.ServersRead)) return [];
        AccessVisibility visibility = authorization.GetVisibility(actorUserId, teamId);
        return servers.List(teamId).Where(item => visibility.CanSeeServer(item.Id)).ToArray();
    }
}
