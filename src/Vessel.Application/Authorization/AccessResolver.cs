using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Teams;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Authorization;

/// <summary>
///     Decides what a user — and the API token of the current request — can see and do in a team (Coolify
///     <c>AccessResolver</c> parity). Owners and admins are unrestricted in their team. A member has the grants stored
///     on the membership (<c>null</c> = team-wide Read). A request token adds a second layer: both layers must allow.
/// </summary>
public interface IAccessResolver
{
    /// <summary>
    ///     The layers that must all allow an action, or <c>null</c> when the actor has no access to the team at all
    ///     (not a member, or the request token belongs to another team). An empty list means unrestricted.
    /// </summary>
    IReadOnlyList<AccessLayer>? LayersFor(UserId userId, TeamId teamId);

    /// <summary>Resolves where a team-owned target sits, or <c>null</c> when it does not exist.</summary>
    ResourceLocation? Locate(ResourceRef resource);

    bool Allows(UserId userId, TeamId teamId, AccessLevel level, ResourceLocation location);

    bool AllowsTeamWide(UserId userId, TeamId teamId, AccessLevel level);

    bool AllowsSomewhere(UserId userId, TeamId teamId, AccessLevel level);

    /// <summary>True when the actor has the level on the whole team or on at least one resource on the server.</summary>
    bool AllowsOnServer(UserId userId, TeamId teamId, AccessLevel level, ServerId serverId);

    /// <summary>What the actor can see in the team, for list filtering.</summary>
    AccessVisibility GetVisibility(UserId userId, TeamId teamId);

    /// <summary>
    ///     True when the member layer of <paramref name="userId" /> (ignoring any request token) holds every level of
    ///     every grant at that scope or above it. A token can never get more access than its owner.
    /// </summary>
    bool MemberHolds(UserId userId, TeamId teamId, IEnumerable<AccessGrant> grants);

    /// <summary>Resolves grants inside a team without any role or token (for validation and tests).</summary>
    AccessLayer BuildLayer(TeamId teamId, IEnumerable<AccessGrant> grants);

    /// <summary>Where a grant's target sits, or <c>null</c> when the target does not exist in the team.</summary>
    ResourceLocation? LocateGrant(TeamId teamId, AccessGrant grant);
}

public sealed class AccessResolver(IVesselDbContext dbContext, IApiTokenAccessContext tokenContext) : IAccessResolver
{
    public static IReadOnlyList<AccessGrant> DefaultMemberGrants { get; } = [AccessGrant.Team(AccessLevel.Read)];

    public IReadOnlyList<AccessLayer>? LayersFor(UserId userId, TeamId teamId)
    {
        ApiTokenAccess? token = tokenContext.Current;
        var tokenApplies = token is not null && token.UserId == userId;
        // A token is bound to its team: it never authorizes anything in another team, even one its owner belongs to.
        if (tokenApplies && token!.TeamId != teamId) return null;

        var layers = new List<AccessLayer>(2);
        if (!TryGetMemberGrants(userId, teamId, out IReadOnlyList<AccessGrant>? memberGrants)) return null;
        if (memberGrants is not null) layers.Add(BuildLayer(teamId, memberGrants));
        if (tokenApplies) layers.Add(BuildLayer(teamId, token!.AsGrants()));
        return layers;
    }

    public ResourceLocation? Locate(ResourceRef resource)
    {
        return resource.Kind switch
        {
            ResourceKind.Project => LocateProject(new ProjectId(resource.Id)),
            ResourceKind.Environment => LocateEnvironment(new EnvironmentId(resource.Id), null),
            ResourceKind.Application => LocateApplication(new ApplicationId(resource.Id)),
            ResourceKind.Database => LocateDatabase(new DatabaseResourceId(resource.Id)),
            ResourceKind.Deployment => LocateDeployment(new DeploymentId(resource.Id)),
            ResourceKind.Server => LocateServer(new ServerId(resource.Id)),
            _ => null
        };
    }

    public bool Allows(UserId userId, TeamId teamId, AccessLevel level, ResourceLocation location)
    {
        if (location.TeamId != teamId) return false;
        IReadOnlyList<AccessLayer>? layers = LayersFor(userId, teamId);
        return layers is not null && layers.All(layer => layer.Allows(level, location));
    }

    public bool AllowsTeamWide(UserId userId, TeamId teamId, AccessLevel level)
    {
        IReadOnlyList<AccessLayer>? layers = LayersFor(userId, teamId);
        return layers is not null && layers.All(layer => layer.AllowsTeamWide(level));
    }

    public bool AllowsSomewhere(UserId userId, TeamId teamId, AccessLevel level)
    {
        IReadOnlyList<AccessLayer>? layers = LayersFor(userId, teamId);
        return layers is not null && layers.All(layer => layer.AllowsSomewhere(level));
    }

    public bool AllowsOnServer(UserId userId, TeamId teamId, AccessLevel level, ServerId serverId)
    {
        IReadOnlyList<AccessLayer>? layers = LayersFor(userId, teamId);
        if (layers is null) return false;
        if (!dbContext.Servers.Any(server => server.Id == serverId && server.TeamId == teamId)) return false;
        if (layers.All(layer => layer.AllowsTeamWide(level))) return true;

        TeamTopology topology = LoadTopology(teamId);
        return ResourcesOnServer(serverId, topology).Any(location =>
            layers.All(layer => layer.Allows(level, location)));
    }

    public AccessVisibility GetVisibility(UserId userId, TeamId teamId)
    {
        IReadOnlyList<AccessLayer>? layers = LayersFor(userId, teamId);
        if (layers is null) return AccessVisibility.None(teamId);
        if (layers.All(layer => layer.CoversWholeTeam)) return AccessVisibility.Unrestricted(teamId);

        TeamTopology topology = LoadTopology(teamId);
        var servers = new HashSet<Guid>();
        foreach (Guid serverId in dbContext.Servers.Where(server => server.TeamId == teamId)
                     .Select(server => server.Id.Value).ToArray())
            if (ResourcesOnServer(new ServerId(serverId), topology)
                .Any(location => layers.All(layer => layer.Allows(AccessLevel.Read, location))))
                servers.Add(serverId);

        return new AccessVisibility(teamId, layers, topology.EnvironmentProjects, servers);
    }

    public bool MemberHolds(UserId userId, TeamId teamId, IEnumerable<AccessGrant> grants)
    {
        if (!TryGetMemberGrants(userId, teamId, out IReadOnlyList<AccessGrant>? memberGrants)) return false;
        if (memberGrants is null) return true;

        AccessLayer layer = BuildLayer(teamId, memberGrants);
        foreach (AccessGrant grant in grants)
        {
            ResourceLocation? location = LocateGrant(teamId, grant);
            if (location is null) return false;
            if (grant.Levels.Any(level => !layer.Allows(level, location.Value))) return false;
        }

        return true;
    }

    public AccessLayer BuildLayer(TeamId teamId, IEnumerable<AccessGrant> grants)
    {
        var teamLevels = new HashSet<AccessLevel>();
        var byProject = new Dictionary<Guid, HashSet<AccessLevel>>();
        var byEnvironment = new Dictionary<Guid, HashSet<AccessLevel>>();
        var byResource = new Dictionary<Guid, HashSet<AccessLevel>>();

        foreach (AccessGrant grant in grants)
        {
            if (grant.Levels.Count == 0) continue;
            IReadOnlySet<AccessLevel> expanded = AccessLevels.Expand(grant.Levels);
            switch (grant.Scope)
            {
                case AccessScope.Team:
                    teamLevels.UnionWith(expanded);
                    break;
                case AccessScope.Project:
                    Add(byProject, grant.TargetId!.Value, expanded);
                    break;
                case AccessScope.Environment:
                    Add(byEnvironment, grant.TargetId!.Value, expanded);
                    break;
                case AccessScope.Resource:
                    Add(byResource, grant.TargetId!.Value, expanded);
                    break;
            }
        }

        var projectLevels = new Dictionary<ProjectId, IReadOnlySet<AccessLevel>>();
        var environmentLevels = new Dictionary<EnvironmentId, IReadOnlySet<AccessLevel>>();
        var resourceLevels = new Dictionary<Guid, IReadOnlySet<AccessLevel>>();
        var visibleProjects = new HashSet<ProjectId>();
        var visibleEnvironments = new HashSet<EnvironmentId>();

        if (byProject.Count + byEnvironment.Count + byResource.Count > 0)
        {
            // Grants that point outside the team (or at deleted targets) are dropped here.
            TeamTopology topology = LoadTopology(teamId);
            foreach ((Guid id, HashSet<AccessLevel> levels) in byProject)
            {
                var projectId = new ProjectId(id);
                if (!topology.Projects.Contains(projectId)) continue;
                projectLevels[projectId] = levels;
                visibleProjects.Add(projectId);
                visibleEnvironments.UnionWith(topology.EnvironmentProjects
                    .Where(pair => pair.Value == projectId)
                    .Select(pair => pair.Key));
            }

            foreach ((Guid id, HashSet<AccessLevel> levels) in byEnvironment)
            {
                var environmentId = new EnvironmentId(id);
                if (!topology.EnvironmentProjects.TryGetValue(environmentId, out ProjectId projectId)) continue;
                environmentLevels[environmentId] = levels;
                visibleEnvironments.Add(environmentId);
                visibleProjects.Add(projectId);
            }

            foreach ((Guid id, HashSet<AccessLevel> levels) in byResource)
            {
                if (!topology.ResourceEnvironments.TryGetValue(id, out EnvironmentId environmentId)) continue;
                resourceLevels[id] = levels;
                visibleEnvironments.Add(environmentId);
                visibleProjects.Add(topology.EnvironmentProjects[environmentId]);
            }
        }

        return new AccessLayer(teamLevels, projectLevels, environmentLevels, resourceLevels, visibleProjects,
            visibleEnvironments);
    }

    /// <summary>
    ///     Member grants: <c>false</c> when not a member; <c>null</c> grants for owners and admins (unrestricted).
    /// </summary>
    private bool TryGetMemberGrants(UserId userId, TeamId teamId, out IReadOnlyList<AccessGrant>? grants)
    {
        var membership = dbContext.TeamMemberships
            .Where(candidate => candidate.UserId == userId && candidate.TeamId == teamId)
            .Select(candidate => new { candidate.Role, candidate.AccessGrants })
            .SingleOrDefault();

        grants = null;
        if (membership is null) return false;
        if (membership.Role is TeamRole.Owner or TeamRole.Admin) return true;

        grants = membership.AccessGrants ?? DefaultMemberGrants;
        return true;
    }

    public ResourceLocation? LocateGrant(TeamId teamId, AccessGrant grant)
    {
        return grant.Scope switch
        {
            AccessScope.Team => new ResourceLocation(teamId),
            AccessScope.Project => LocateProject(new ProjectId(grant.TargetId!.Value)) is { } project
                                   && project.TeamId == teamId
                ? project
                : null,
            AccessScope.Environment => LocateEnvironment(new EnvironmentId(grant.TargetId!.Value), null) is
            { } environment && environment.TeamId == teamId
                ? environment
                : null,
            AccessScope.Resource => LocateAnyResource(grant.TargetId!.Value) is { } resource
                                    && resource.TeamId == teamId
                ? resource
                : null,
            _ => null
        };
    }

    private ResourceLocation? LocateProject(ProjectId projectId)
    {
        TeamId? teamId = dbContext.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (TeamId?)project.TeamId)
            .SingleOrDefault();
        return teamId.HasValue ? new ResourceLocation(teamId.Value, projectId) : null;
    }

    private ResourceLocation? LocateEnvironment(EnvironmentId environmentId, Guid? resourceId)
    {
        ProjectId? projectId = dbContext.Environments
            .Where(environment => environment.Id == environmentId)
            .Select(environment => (ProjectId?)environment.ProjectId)
            .SingleOrDefault();
        if (!projectId.HasValue) return null;

        ResourceLocation? project = LocateProject(projectId.Value);
        return project is null
            ? null
            : new ResourceLocation(project.Value.TeamId, projectId, environmentId, resourceId);
    }

    private ResourceLocation? LocateApplication(ApplicationId applicationId)
    {
        EnvironmentId? environmentId = dbContext.Applications
            .Where(application => application.Id == applicationId)
            .Select(application => (EnvironmentId?)application.EnvironmentId)
            .SingleOrDefault();
        return environmentId.HasValue ? LocateEnvironment(environmentId.Value, applicationId.Value) : null;
    }

    private ResourceLocation? LocateDatabase(DatabaseResourceId databaseId)
    {
        EnvironmentId? environmentId = dbContext.DatabaseResources
            .Where(database => database.Id == databaseId)
            .Select(database => (EnvironmentId?)database.EnvironmentId)
            .SingleOrDefault();
        return environmentId.HasValue ? LocateEnvironment(environmentId.Value, databaseId.Value) : null;
    }

    private ResourceLocation? LocateService(ServiceResourceId serviceId)
    {
        EnvironmentId? environmentId = dbContext.ServiceResources
            .Where(service => service.Id == serviceId)
            .Select(service => (EnvironmentId?)service.EnvironmentId)
            .SingleOrDefault();
        return environmentId.HasValue ? LocateEnvironment(environmentId.Value, serviceId.Value) : null;
    }

    private ResourceLocation? LocateAnyResource(Guid resourceId)
    {
        return LocateApplication(new ApplicationId(resourceId))
               ?? LocateDatabase(new DatabaseResourceId(resourceId))
               ?? LocateService(new ServiceResourceId(resourceId));
    }

    private ResourceLocation? LocateDeployment(DeploymentId deploymentId)
    {
        ApplicationId? applicationId = dbContext.Deployments
            .Where(deployment => deployment.Id == deploymentId)
            .Select(deployment => (ApplicationId?)deployment.ApplicationId)
            .SingleOrDefault();
        return applicationId.HasValue ? LocateApplication(applicationId.Value) : null;
    }

    private ResourceLocation? LocateServer(ServerId serverId)
    {
        TeamId? teamId = dbContext.Servers
            .Where(server => server.Id == serverId)
            .Select(server => (TeamId?)server.TeamId)
            .SingleOrDefault();
        return teamId.HasValue ? new ResourceLocation(teamId.Value) : null;
    }

    private IEnumerable<ResourceLocation> ResourcesOnServer(ServerId serverId, TeamTopology topology)
    {
        IEnumerable<(Guid Id, EnvironmentId EnvironmentId)> resources = dbContext.Applications
            .Where(application => application.ServerId == serverId)
            .Select(application => new { Id = application.Id.Value, application.EnvironmentId })
            .AsEnumerable()
            .Select(item => (item.Id, item.EnvironmentId))
            .Concat(dbContext.DatabaseResources
                .Where(database => database.ServerId == serverId)
                .Select(database => new { Id = database.Id.Value, database.EnvironmentId })
                .AsEnumerable()
                .Select(item => (item.Id, item.EnvironmentId)))
            .Concat(dbContext.ServiceResources
                .Where(service => service.ServerId == serverId)
                .Select(service => new { Id = service.Id.Value, service.EnvironmentId })
                .AsEnumerable()
                .Select(item => (item.Id, item.EnvironmentId)));

        foreach ((Guid id, EnvironmentId environmentId) in resources)
            if (topology.EnvironmentProjects.TryGetValue(environmentId, out ProjectId projectId))
                yield return new ResourceLocation(topology.TeamId, projectId, environmentId, id);
    }

    private TeamTopology LoadTopology(TeamId teamId)
    {
        ProjectId[] projects = dbContext.Projects
            .Where(project => project.TeamId == teamId)
            .Select(project => project.Id)
            .ToArray();
        var environments = dbContext.Environments
            .Where(environment => projects.Contains(environment.ProjectId))
            .Select(environment => new { environment.Id, environment.ProjectId })
            .ToArray()
            .ToDictionary(environment => environment.Id, environment => environment.ProjectId);
        EnvironmentId[] environmentIds = environments.Keys.ToArray();

        var resources = new Dictionary<Guid, EnvironmentId>();
        foreach (var application in dbContext.Applications
                     .Where(application => environmentIds.Contains(application.EnvironmentId))
                     .Select(application => new { Id = application.Id.Value, application.EnvironmentId })
                     .ToArray())
            resources[application.Id] = application.EnvironmentId;
        foreach (var database in dbContext.DatabaseResources
                     .Where(database => environmentIds.Contains(database.EnvironmentId))
                     .Select(database => new { Id = database.Id.Value, database.EnvironmentId })
                     .ToArray())
            resources[database.Id] = database.EnvironmentId;
        foreach (var service in dbContext.ServiceResources
                     .Where(service => environmentIds.Contains(service.EnvironmentId))
                     .Select(service => new { Id = service.Id.Value, service.EnvironmentId })
                     .ToArray())
            resources[service.Id] = service.EnvironmentId;

        return new TeamTopology(teamId, projects.ToHashSet(), environments, resources);
    }

    private static void Add(Dictionary<Guid, HashSet<AccessLevel>> target, Guid id, IReadOnlySet<AccessLevel> levels)
    {
        if (!target.TryGetValue(id, out HashSet<AccessLevel>? existing))
        {
            existing = [];
            target[id] = existing;
        }

        existing.UnionWith(levels);
    }

    private sealed record TeamTopology(
        TeamId TeamId,
        IReadOnlySet<ProjectId> Projects,
        IReadOnlyDictionary<EnvironmentId, ProjectId> EnvironmentProjects,
        IReadOnlyDictionary<Guid, EnvironmentId> ResourceEnvironments);
}
