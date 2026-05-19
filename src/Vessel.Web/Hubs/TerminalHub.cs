using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Application.Realtime;

namespace Vessel.Web.Hubs;

[Authorize(Policy = VesselPermissions.TerminalsOpen)]
public sealed class TerminalHub : AuthorizedResourceHub
{
    public TerminalHub(VesselAuthorizationService authorizationService)
        : base(authorizationService)
    {
    }

    public Task JoinTerminal(Guid terminalSessionId)
    {
        return Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Terminal(terminalSessionId),
            Context.ConnectionAborted);
    }
}
