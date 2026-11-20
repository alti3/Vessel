using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Teams;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Authorization;

public sealed class VesselAuthorizationService
{
    private readonly IVesselDbContext _unitOfWork;

    public VesselAuthorizationService(IVesselDbContext unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public bool HasPermission(UserId userId, TeamId teamId, string permission)
    {
        TeamRole? role = _unitOfWork.TeamMemberships
            .Where(membership => membership.UserId == userId && membership.TeamId == teamId)
            .Select(membership => (TeamRole?)membership.Role)
            .SingleOrDefault();

        return role.HasValue && VesselPermissions.ForRole(role.Value).Contains(permission);
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

    public bool CanAccessProject(UserId userId, ProjectId projectId)
    {
        TeamId? teamId = _unitOfWork.Projects
            .Where(project => project.Id == projectId)
            .Select(project => (TeamId?)project.TeamId)
            .SingleOrDefault();

        return teamId.HasValue && CanAccessTeam(userId, teamId.Value);
    }

    public bool CanAccessServer(UserId userId, ServerId serverId)
    {
        TeamId? teamId = _unitOfWork.Servers
            .Where(server => server.Id == serverId)
            .Select(server => (TeamId?)server.TeamId)
            .SingleOrDefault();

        return teamId.HasValue && CanAccessTeam(userId, teamId.Value);
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

    public bool CanAccessApplication(UserId userId, ApplicationId applicationId)
    {
        TeamId? teamId = _unitOfWork.Applications
            .Where(application => application.Id == applicationId)
            .Join(
                _unitOfWork.Environments,
                application => application.EnvironmentId,
                environment => environment.Id,
                (_, environment) => environment.ProjectId)
            .Join(
                _unitOfWork.Projects,
                projectId => projectId,
                project => project.Id,
                (_, project) => (TeamId?)project.TeamId)
            .SingleOrDefault();

        return teamId.HasValue && CanAccessTeam(userId, teamId.Value);
    }

    public bool CanAccessDeployment(UserId userId, DeploymentId deploymentId)
    {
        ApplicationId? applicationId = _unitOfWork.Deployments
            .Where(deployment => deployment.Id == deploymentId)
            .Select(deployment => (ApplicationId?)deployment.ApplicationId)
            .SingleOrDefault();

        return applicationId.HasValue && CanAccessApplication(userId, applicationId.Value);
    }
}
