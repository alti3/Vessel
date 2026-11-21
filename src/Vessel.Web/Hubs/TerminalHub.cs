using Microsoft.AspNetCore.Authorization;
using Vessel.Application.Authorization;
using Vessel.Application.Realtime;
using Vessel.Application.Terminals;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Hubs;

/// <summary>
///     Terminal I/O. The browser never sends commands: it joins a session it opened through the API by redeeming the
///     session's single-use attach token (bound to the user, team and session), which starts the server-derived
///     command. Input and resize are re-authorized by <see cref="TerminalSessionManager" />; sessions joined by a
///     connection are closed when that connection goes away.
/// </summary>
[Authorize(Policy = VesselPermissions.TerminalsOpen)]
public sealed class TerminalHub : AuthorizedResourceHub
{
    private const string JoinedSessionsKey = "vessel.terminal.sessions";

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

    public async Task<bool> JoinTerminal(Guid terminalSessionId, string token)
    {
        var sessionId = new TerminalSessionId(terminalSessionId);
        var user = Context.User!;
        if (!_authorizationService.CanAccessTerminalSession(user.GetUserId(), user.GetTeamId(), sessionId)) return false;

        // Join first so the first output of the starting process reaches this connection.
        var group = RealtimeGroupNames.Terminal(terminalSessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        try
        {
            await _terminalSessionManager.AttachAsync(user.GetUserId(), user.GetTeamId(), sessionId, token,
                Context.ConnectionAborted);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, CancellationToken.None);
            return false;
        }

        JoinedSessions().Add(terminalSessionId);
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
        JoinedSessions().Remove(terminalSessionId);
        return _terminalSessionManager.CloseAsync(
            user.GetUserId(),
            user.GetTeamId(),
            new TerminalSessionId(terminalSessionId),
            Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var user = Context.User;
        if (user is not null && Context.Items.TryGetValue(JoinedSessionsKey, out var value) &&
            value is HashSet<Guid> sessions)
            foreach (Guid sessionId in sessions.ToArray())
                try
                {
                    await _terminalSessionManager.CloseAsync(user.GetUserId(), user.GetTeamId(),
                        new TerminalSessionId(sessionId), CancellationToken.None);
                }
                catch (Exception closeError) when (closeError is UnauthorizedAccessException or
                                                       InvalidOperationException)
                {
                    // Access already lost or session gone; the revalidator ends what is left.
                }

        await base.OnDisconnectedAsync(exception);
    }

    private HashSet<Guid> JoinedSessions()
    {
        if (Context.Items.TryGetValue(JoinedSessionsKey, out var value) && value is HashSet<Guid> sessions)
            return sessions;
        var created = new HashSet<Guid>();
        Context.Items[JoinedSessionsKey] = created;
        return created;
    }
}
