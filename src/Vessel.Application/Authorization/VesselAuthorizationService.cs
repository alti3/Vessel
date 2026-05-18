using Vessel.Application.Persistence;
using Vessel.Domain;
using Vessel.Domain.Teams;

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

    public bool CanAccessTeam(UserId userId, TeamId teamId)
    {
        return _unitOfWork.TeamMemberships.Any(membership => membership.UserId == userId && membership.TeamId == teamId);
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

    public bool CanAccessApplication(UserId userId, Vessel.Domain.ApplicationId applicationId)
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
        Vessel.Domain.ApplicationId? applicationId = _unitOfWork.Deployments
            .Where(deployment => deployment.Id == deploymentId)
            .Select(deployment => (Vessel.Domain.ApplicationId?)deployment.ApplicationId)
            .SingleOrDefault();

        return applicationId.HasValue && CanAccessApplication(userId, applicationId.Value);
    }
}
