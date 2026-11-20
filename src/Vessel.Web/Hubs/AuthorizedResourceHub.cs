using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Vessel.Application.Authorization;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Web.Security;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Web.Hubs;

[Authorize]
public abstract class AuthorizedResourceHub : Hub
{
    private readonly VesselAuthorizationService _authorizationService;

    protected AuthorizedResourceHub(VesselAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    protected Task JoinTenantGroupAsync()
    {
        return Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Tenant(Context.User!.GetTeamId()),
            Context.ConnectionAborted);
    }

    protected async Task<bool> JoinProjectGroupAsync(ProjectId projectId)
    {
        if (!CanJoin(ResourceRef.Project(projectId), VesselPermissions.ProjectsRead)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Project(projectId),
            Context.ConnectionAborted);
        return true;
    }

    protected async Task<bool> JoinServerGroupAsync(ServerId serverId)
    {
        if (!CanJoin(ResourceRef.Server(serverId), VesselPermissions.ServersRead)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Server(serverId),
            Context.ConnectionAborted);
        return true;
    }

    protected async Task<bool> JoinApplicationGroupAsync(ApplicationId applicationId)
    {
        if (!CanJoin(ResourceRef.Application(applicationId), VesselPermissions.ApplicationsRead)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Application(applicationId),
            Context.ConnectionAborted);
        return true;
    }

    protected async Task<bool> JoinDeploymentGroupAsync(DeploymentId deploymentId)
    {
        if (!CanJoin(ResourceRef.Deployment(deploymentId), VesselPermissions.DeploymentsReadLogs)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Deployment(deploymentId),
            Context.ConnectionAborted);
        return true;
    }

    private bool CanJoin(ResourceRef resource, string permission)
    {
        // Realtime groups follow the same rule as API calls: the resource must belong to the connection's active
        // team and the caller's role in that team must grant read access.
        ClaimsPrincipal user = Context.User!;
        return _authorizationService.IsAuthorized(user.GetUserId(), user.GetTeamId(), resource, permission);
    }

    protected Task JoinUserGroupAsync()
    {
        return Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.User(Context.User!.GetUserId()),
            Context.ConnectionAborted);
    }
}
