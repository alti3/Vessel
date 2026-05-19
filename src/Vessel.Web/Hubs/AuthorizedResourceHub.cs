using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Vessel.Application.Authorization;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Web.Security;

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
        if (!_authorizationService.CanAccessProject(Context.User!.GetUserId(), projectId)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Project(projectId),
            Context.ConnectionAborted);
        return true;
    }

    protected async Task<bool> JoinServerGroupAsync(ServerId serverId)
    {
        if (!_authorizationService.CanAccessServer(Context.User!.GetUserId(), serverId)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Server(serverId),
            Context.ConnectionAborted);
        return true;
    }

    protected async Task<bool> JoinApplicationGroupAsync(Vessel.Domain.ApplicationId applicationId)
    {
        if (!_authorizationService.CanAccessApplication(Context.User!.GetUserId(), applicationId)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Application(applicationId),
            Context.ConnectionAborted);
        return true;
    }

    protected async Task<bool> JoinDeploymentGroupAsync(DeploymentId deploymentId)
    {
        if (!_authorizationService.CanAccessDeployment(Context.User!.GetUserId(), deploymentId)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Deployment(deploymentId),
            Context.ConnectionAborted);
        return true;
    }

    protected Task JoinUserGroupAsync()
    {
        return Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.User(Context.User!.GetUserId()),
            Context.ConnectionAborted);
    }
}
