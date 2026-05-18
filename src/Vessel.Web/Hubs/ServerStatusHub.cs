using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Domain;

namespace Vessel.Web.Hubs;

[Authorize(Policy = VesselPermissions.ServersRead)]
public sealed class ServerStatusHub : AuthorizedResourceHub
{
    public ServerStatusHub(VesselAuthorizationService authorizationService)
        : base(authorizationService)
    {
    }

    public async Task<bool> JoinServer(Guid serverId)
    {
        return await JoinServerGroupAsync(new ServerId(serverId));
    }
}
