using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Teams;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Authorization;

/// <summary>
///     Team-scoped authorization. Resource checks resolve the team that <b>owns</b> the resource and evaluate the
///     actor's role in that team. The owning team must also be the caller's active (session or token) team, so a role
///     held in one team — including the caller's personal team — never authorizes access to another team's resources.
/// </summary>
public sealed class VesselAuthorizationService
{
    private readonly IVesselDbContext _unitOfWork;

    public VesselAuthorizationService(IVesselDbContext unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    ///     Team-level permission check for team-scoped operations (listing or creating resources in the active team).
    ///     Use <see cref="Demand" /> or <see cref="IsAuthorized" /> for operations on an existing resource.
    /// </summary>
    public bool HasPermission(UserId userId, TeamId teamId, string permission)
    {
        TeamRole? role = GetRole(userId, teamId);
        return role.HasValue && VesselPermissions.ForRole(role.Value).Contains(permission);
    }

    public TeamRole? GetRole(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId && membership.TeamId == teamId)
            .Select(membership => (TeamRole?)membership.Role)
            .SingleOrDefault();
    }

    public bool CanAccessTeam(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships.Any(membership =>
            membership.UserId == userId && membership.TeamId == teamId);
    }

    /// <summary>
    ///     Returns <c>true</c> only when the resource exists, is owned by <paramref name="activeTeamId" />, and the
    ///     actor's role in that owning team grants <paramref name="permission" />.
    /// </summary>
    public bool IsAuthorized(UserId userId, TeamId activeTeamId, ResourceRef resource, string permission)
    {
        return Evaluate(userId, activeTeamId, resource, permission) == AuthorizationOutcome.Allowed;
    }

    /// <summary>
    ///     Throws <see cref="UnauthorizedAccessException" /> unless <see cref="IsAuthorized" /> would succeed. Missing
    ///     resources and resources owned by another team produce the same "outside the active team" failure.
    /// </summary>
    public void Demand(UserId userId, TeamId activeTeamId, ResourceRef resource, string permission)
    {
        switch (Evaluate(userId, activeTeamId, resource, permission))
        {
            case AuthorizationOutcome.Allowed:
                return;
            case AuthorizationOutcome.MissingPermission:
                throw new UnauthorizedAccessException($"Missing required permission '{permission}'.");
            default:
                throw new UnauthorizedAccessException($"{resource.Kind} is outside the active team.");
        }
    }

    /// <summary>Resolves the team that owns <paramref name="resource" />, or <c>null</c> when it does not exist.</summary>
    public TeamId? ResolveOwningTeam(ResourceRef resource)
    {
        return resource.Kind switch
        {
            ResourceKind.Project => ProjectTeam(new ProjectId(resource.Id)),
            ResourceKind.Environment => EnvironmentTeam(new EnvironmentId(resource.Id)),
            ResourceKind.Server => ServerTeam(new ServerId(resource.Id)),
            ResourceKind.Application => ApplicationTeam(new ApplicationId(resource.Id)),
            ResourceKind.Deployment => DeploymentTeam(new DeploymentId(resource.Id)),
            ResourceKind.Database => DatabaseTeam(new DatabaseResourceId(resource.Id)),
            _ => null
        };
    }

    public bool CanAccessTerminalSession(UserId userId, TeamId teamId, TerminalSessionId terminalSessionId)
    {
        var session = _unitOfWork.TerminalSessions
            .Where(session => session.Id == terminalSessionId)
            .Select(session => new { session.TeamId, session.OwnerUserId })
            .SingleOrDefault();

        return session is not null
               && session.TeamId == teamId
               && session.OwnerUserId == userId
               && CanAccessTeam(userId, teamId);
    }

    private AuthorizationOutcome Evaluate(UserId userId, TeamId activeTeamId, ResourceRef resource, string permission)
    {
        // The permission check runs first so callers keep a stable "missing permission" failure.
        if (!HasPermission(userId, activeTeamId, permission)) return AuthorizationOutcome.MissingPermission;

        // Fail closed: the resource must be owned by the active team. Because the permission above was evaluated
        // against the active team, the role used is always the actor's role in the owning team; membership in any
        // other team (for example the actor's personal team) never falls through.
        TeamId? owningTeamId = ResolveOwningTeam(resource);
        return owningTeamId.HasValue && owningTeamId.Value == activeTeamId
            ? AuthorizationOutcome.Allowed
            : AuthorizationOutcome.OutsideTeam;
    }

    private TeamId? ProjectTeam(ProjectId projectId)
    {
        return _unitOfWork.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (TeamId?)project.TeamId)
            .SingleOrDefault();
    }

    private TeamId? EnvironmentTeam(EnvironmentId environmentId)
    {
        ProjectId? projectId = _unitOfWork.Environments
            .Where(environment => environment.Id == environmentId)
            .Select(environment => (ProjectId?)environment.ProjectId)
            .SingleOrDefault();

        return projectId.HasValue ? ProjectTeam(projectId.Value) : null;
    }

    private TeamId? ServerTeam(ServerId serverId)
    {
        return _unitOfWork.Servers
            .Where(server => server.Id == serverId)
            .Select(server => (TeamId?)server.TeamId)
            .SingleOrDefault();
    }

    private TeamId? ApplicationTeam(ApplicationId applicationId)
    {
        EnvironmentId? environmentId = _unitOfWork.Applications
            .Where(application => application.Id == applicationId)
            .Select(application => (EnvironmentId?)application.EnvironmentId)
            .SingleOrDefault();

        return environmentId.HasValue ? EnvironmentTeam(environmentId.Value) : null;
    }

    private TeamId? DeploymentTeam(DeploymentId deploymentId)
    {
        ApplicationId? applicationId = _unitOfWork.Deployments
            .Where(deployment => deployment.Id == deploymentId)
            .Select(deployment => (ApplicationId?)deployment.ApplicationId)
            .SingleOrDefault();

        return applicationId.HasValue ? ApplicationTeam(applicationId.Value) : null;
    }

    private TeamId? DatabaseTeam(DatabaseResourceId databaseId)
    {
        EnvironmentId? environmentId = _unitOfWork.DatabaseResources
            .Where(database => database.Id == databaseId)
            .Select(database => (EnvironmentId?)database.EnvironmentId)
            .SingleOrDefault();

        return environmentId.HasValue ? EnvironmentTeam(environmentId.Value) : null;
    }

    private enum AuthorizationOutcome
    {
        Allowed,
        MissingPermission,
        OutsideTeam
    }
}
