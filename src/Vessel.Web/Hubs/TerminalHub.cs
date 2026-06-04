using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Application.Realtime;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Hubs;

[Authorize(Policy = VesselPermissions.TerminalsOpen)]
public sealed class TerminalHub : AuthorizedResourceHub
{
    private readonly VesselAuthorizationService _authorizationService;
    private readonly TerminalSessionManager _terminalSessionManager;

    public TerminalHub(
        VesselAuthorizationService authorizationService,
        TerminalSessionManager terminalSessionManager)
        : base(authorizationService)
    {
        _authorizationService = authorizationService;
        _terminalSessionManager = terminalSessionManager;
    }

    public async Task<bool> JoinTerminal(Guid terminalSessionId)
    {
        var sessionId = new TerminalSessionId(terminalSessionId);
        var user = Context.User!;
        if (!_authorizationService.CanAccessTerminalSession(user.GetUserId(), sessionId)) return false;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            RealtimeGroupNames.Terminal(terminalSessionId),
            Context.ConnectionAborted);
        return true;
    }

    public Task SendInput(Guid terminalSessionId, string data)
    {
        var user = Context.User!;
        return _terminalSessionManager.SendInputAsync(
            user.GetUserId(),
            user.GetTeamId(),
            new TerminalSessionId(terminalSessionId),
            data,
            Context.ConnectionAborted);
    }

    public Task Resize(Guid terminalSessionId, int columns, int rows)
    {
        var user = Context.User!;
        return _terminalSessionManager.ResizeAsync(
            user.GetUserId(),
            user.GetTeamId(),
            new TerminalSessionId(terminalSessionId),
            columns,
            rows,
            Context.ConnectionAborted);
    }

    public Task Close(Guid terminalSessionId)
    {
        var user = Context.User!;
        return _terminalSessionManager.CloseAsync(
            user.GetUserId(),
            user.GetTeamId(),
            new TerminalSessionId(terminalSessionId),
            Context.ConnectionAborted);
    }
}
