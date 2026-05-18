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
}
