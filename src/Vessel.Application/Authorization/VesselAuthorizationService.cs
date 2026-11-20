using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Access;
using Vessel.Domain.Teams;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Authorization;

/// <summary>
///     Team-scoped authorization. Resource checks resolve the team that <b>owns</b> the resource and evaluate the
///     actor's access in that team. The owning team must also be the caller's active (session or token) team, so a role
///     held in one team — including the caller's personal team — never authorizes access to another team's resources.
///     <para>
///         Access is evaluated by <see cref="IAccessResolver" /> (IAM-05): owners and admins are unrestricted in their
///         team, members have their fine-grained grants (default team-wide read), and the API token of the current
///         request is a second layer that must also allow the action. Every <see cref="Demand" /> call site is therefore
///         grant- and token-aware.
///     </para>
/// </summary>
public sealed class VesselAuthorizationService
{
    private readonly IAccessResolver _access;
    private readonly IApiTokenAccessContext _tokenContext;
    private readonly IVesselDbContext _unitOfWork;

    /// <summary>Authorization without a request token layer (background work, tests).</summary>
    public VesselAuthorizationService(IVesselDbContext unitOfWork)
        : this(unitOfWork, new AccessResolver(unitOfWork, NoApiTokenAccessContext.Instance),
            NoApiTokenAccessContext.Instance)
    {
    }

    public VesselAuthorizationService(
        IVesselDbContext unitOfWork,
        IAccessResolver access,
        IApiTokenAccessContext tokenContext)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _tokenContext = tokenContext;
    }

    public IAccessResolver Access => _access;

    /// <summary>
    ///     Team-level permission check for team-scoped operations (creating resources in the active team, team-level
    ///     settings). Located permissions must be held on the <b>whole team</b>; scoped grants never satisfy this check.
    ///     Use <see cref="Demand" /> or <see cref="IsAuthorized" /> for operations on an existing resource and
    ///     <see cref="HasPermissionSomewhere" /> for list endpoints that filter by <see cref="GetVisibility" />.
    /// </summary>
    public bool HasPermission(UserId userId, TeamId teamId, string permission)
    {
        return Check(userId, teamId, permission, false);
    }

    /// <summary>
    ///     True when the actor holds <paramref name="permission" /> on at least one scope in the team (any project,
    ///     environment or resource for located permissions; the whole team otherwise). Used by endpoint gates and
    ///     list queries; resource-level checks still go through <see cref="Demand" />.
    /// </summary>
    public bool HasPermissionSomewhere(UserId userId, TeamId teamId, string permission)
    {
        return Check(userId, teamId, permission, true);
    }

    /// <summary>
    ///     Instance administration (IAM-06): the user is an instance administrator and, when the request is authenticated
    ///     by an API token, that token carries <c>root</c>. Team roles never imply this.
    /// </summary>
    public bool IsInstanceManager(UserId userId)
    {
        return Check(userId, default, VesselPermissions.InstanceManage, false);
    }

    /// <summary>What the actor (and the request token) can see in the team, for list filtering.</summary>
    public AccessVisibility GetVisibility(UserId userId, TeamId teamId)
    {
        return _access.GetVisibility(userId, teamId);
    }

    /// <summary>
    ///     Sensitive-response policy (IAM-04): secrets, connection details and similar fields are returned only when
    ///     the actor — and the request token (<c>read:sensitive</c> or <c>root</c>) — hold ReadSensitive on the target
    ///     (or team-wide when <paramref name="resource" /> is <c>null</c>).
    /// </summary>
    public bool CanReadSensitive(UserId userId, TeamId teamId, ResourceRef? resource = null)
    {
        return resource.HasValue
            ? IsAuthorized(userId, teamId, resource.Value, VesselPermissions.SecretsRead)
            : HasPermission(userId, teamId, VesselPermissions.SecretsRead);
    }

    public TeamRole? GetRole(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId && membership.TeamId == teamId)
            .Select(membership => (TeamRole?)membership.Role)
            .SingleOrDefault();
    }

    /// <summary>
    /// Instance administrators are trusted with host-local execution (control-plane runtime, host terminals)
    /// and instance-wide maintenance. Team roles never imply this privilege.
    /// </summary>
    public bool IsInstanceAdmin(UserId userId)
    {
        return _unitOfWork.Users.Any(user => user.Id == userId && user.IsInstanceAdmin);
    }

    /// <summary>
    /// True when an Owner or Admin of the team is an instance administrator. Used for unattended
    /// (webhook/scheduled) work on host-local servers where no interactive actor is present.
    /// </summary>
    public bool TeamHasInstanceAdmin(TeamId teamId)
    {
        UserId[] candidates = _unitOfWork.TeamMemberships
            .Where(membership => membership.TeamId == teamId
                                 && (membership.Role == TeamRole.Owner || membership.Role == TeamRole.Admin))
            .Select(membership => membership.UserId)
            .ToArray();

        return candidates.Length > 0
               && _unitOfWork.Users.Any(user => candidates.Contains(user.Id) && user.IsInstanceAdmin);
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

    private bool Check(UserId userId, TeamId teamId, string permission, bool anyScope)
    {
        PermissionRule? rule = VesselPermissions.GetRule(permission);
        if (rule is null) return false;

        switch (rule.Reach)
        {
            case PermissionReach.InstanceAdmin:
                // Instance administration is independent of team roles, but a request token must carry root.
                ApiTokenAccess? token = _tokenContext.Current;
                return IsInstanceAdmin(userId)
                       && (token is null || (token.UserId == userId && token.Levels.Contains(AccessLevel.Root)));
            case PermissionReach.TeamAdmin:
                return GetRole(userId, teamId) is TeamRole.Owner or TeamRole.Admin
                       && _access.AllowsTeamWide(userId, teamId, rule.Level);
            case PermissionReach.TeamWide:
                return _access.AllowsTeamWide(userId, teamId, rule.Level);
            default:
                return anyScope
                    ? _access.AllowsSomewhere(userId, teamId, rule.Level)
                    : _access.AllowsTeamWide(userId, teamId, rule.Level);
        }
    }

    private AuthorizationOutcome Evaluate(UserId userId, TeamId activeTeamId, ResourceRef resource, string permission)
    {
        // The permission check runs first so callers keep a stable "missing permission" failure.
        if (!HasPermissionSomewhere(userId, activeTeamId, permission)) return AuthorizationOutcome.MissingPermission;

        // Fail closed: the resource must be owned by the active team. Access is always evaluated in the active
        // team, so membership in any other team (for example the actor's personal team) never falls through.
        ResourceLocation? location = _access.Locate(resource);
        if (location is null || location.Value.TeamId != activeTeamId) return AuthorizationOutcome.OutsideTeam;

        PermissionRule rule = VesselPermissions.GetRule(permission)!;
        if (rule.Reach != PermissionReach.Located) return AuthorizationOutcome.Allowed;

        // Servers are team-level: a scoped actor may use one only when it runs a resource the actor can reach.
        var allowed = resource.Kind == ResourceKind.Server
            ? _access.AllowsOnServer(userId, activeTeamId, rule.Level, new ServerId(resource.Id))
            : _access.Allows(userId, activeTeamId, rule.Level, location.Value);
        return allowed ? AuthorizationOutcome.Allowed : AuthorizationOutcome.MissingPermission;
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
