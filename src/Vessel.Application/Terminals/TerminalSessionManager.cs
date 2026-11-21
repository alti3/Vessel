using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Databases;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using Vessel.Domain.Terminals;
using AppEntity = Vessel.Domain.Applications.Application;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Terminals;

/// <summary>
///     Terminal sessions (SRV-06). Opening a session authorizes the target and returns a short-lived single-use attach
///     token; the terminal process starts only when that token is redeemed by the same user (hub <c>JoinTerminal</c>).
///     Commands are derived server-side from the target (server shell, container exec with a shell fallback, or
///     container attach) as structured arguments. Access is re-evaluated on every input and periodically by
///     <see cref="RevalidateAsync" />, which ends sessions whose user lost access, whose server disabled terminals, or
///     that went idle.
/// </summary>
public sealed partial class TerminalSessionManager(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ITerminalProcessBridge bridge,
    IRealtimeNotifier realtime,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider,
    TerminalAttachTokens? attachTokens = null,
    IServerCommandRunner? serverCommands = null)
{
    public const string AccessLostReason = "Terminal session ended: access to the target was lost.";
    public const string IdleReason = "Terminal session ended after being idle.";
    public const string LifetimeReason = "Terminal session reached its maximum lifetime.";
    public const string NotAttachedReason = "Terminal session was never attached.";

    private static readonly TimeSpan ContainerLookupTimeout = TimeSpan.FromSeconds(30);

    private readonly TerminalSecurityPolicy _policy = TerminalSecurityPolicy.Default;
    private readonly TerminalAttachTokens _tokens = attachTokens ?? new TerminalAttachTokens(timeProvider);

    public async Task<TerminalSessionDetails> OpenAsync(
        UserId actorUserId,
        TeamId teamId,
        OpenTerminalSessionRequest request,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serverId = new ServerId(request.ServerId);
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.TerminalsOpen);

        Server server = dbContext.Servers.SingleOrDefault(server => server.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        if (server.TeamId != teamId)
            throw new UnauthorizedAccessException("Server is outside the active team.");

        // Fail closed: a terminal is only ever opened over the target's own connection; remote (SSH) and container
        // sessions need the PTY bridge, and are rejected instead of falling back to a shell on the Vessel host.
        if (!server.IsHostLocal && server.ConnectionType != ServerConnectionType.Ssh)
            throw new InvalidOperationException(TerminalTargetPolicy.RemoteNotSupportedMessage);
        if (!server.IsHostLocal && !bridge.SupportsRemoteAndContainerSessions)
            throw new InvalidOperationException(TerminalTargetPolicy.RemoteNotSupportedMessage);
        if (request.TargetType == TerminalTargetType.Container && !bridge.SupportsRemoteAndContainerSessions)
            throw new InvalidOperationException(TerminalTargetPolicy.ContainerNotSupportedMessage);
        // Host terminals are host-root-equivalent: only instance administrators may open them.
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        if (!server.TerminalAccessEnabled)
            throw new UnauthorizedAccessException(TerminalTargetPolicy.TerminalDisabledMessage);

        string? containerName = null;
        if (request.TargetType == TerminalTargetType.Container)
        {
            if (request.ResourceKind is null || request.ResourceId is null)
                throw new InvalidOperationException("Container terminals require the owning resource.");
            var denial = ResourceDenial(actorUserId, teamId, serverId, request.ResourceKind, request.ResourceId);
            if (denial is not null) throw new UnauthorizedAccessException(denial);
            containerName = await ResolveContainerAsync(server, request.ResourceKind.Value, request.ResourceId.Value,
                request.ContainerName, cancellationToken);
        }
        else if (request.Mode != TerminalMode.Shell)
        {
            throw new InvalidOperationException("Server terminals only support shell mode.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TerminalSession session = TerminalSession.Open(
            teamId,
            actorUserId,
            serverId,
            request.TargetType,
            containerName,
            Describe(server, request.TargetType, request.Mode, containerName),
            request.Columns,
            request.Rows,
            now,
            request.Mode,
            request.ResourceKind,
            request.ResourceId);

        await dbContext.TerminalSessionRepository.AddAsync(session, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TerminalSessionOpened,
            new AuditTarget("terminal_session", session.Id.Value.ToString("D")),
            correlationId,
            Metadata(session),
            cancellationToken);

        (string token, DateTimeOffset expiresAt) = _tokens.Issue(actorUserId, teamId, session.Id);
        return ToDetails(session) with { AttachToken = token, AttachTokenExpiresAt = expiresAt };
    }

    /// <summary>
    ///     Redeems an attach token. For a new session this re-checks access and starts the terminal process; for a
    ///     connected session (reconnect) it only proves the caller may join its output.
    /// </summary>
    public async Task<TerminalSessionDetails> AttachAsync(
        UserId actorUserId,
        TeamId teamId,
        TerminalSessionId sessionId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        RequireSessionOwner(actorUserId, teamId, sessionId);
        if (!_tokens.TryRedeem(token, actorUserId, teamId, sessionId))
            throw new UnauthorizedAccessException("The terminal attach token is invalid or expired.");

        TerminalSession session = LoadSession(sessionId);
        if (session.Status == TerminalSessionStatus.Connected) return ToDetails(session);
        if (session.Status != TerminalSessionStatus.Opening)
            throw new InvalidOperationException("Terminal session has ended.");

        var denial = AccessDenial(actorUserId, teamId, session);
        if (denial is not null)
        {
            await EndAsync(session, actorUserId, AuditActions.TerminalSessionRevoked, denial, cancellationToken);
            throw new UnauthorizedAccessException(denial);
        }

        Server server = dbContext.Servers.Single(candidate => candidate.Id == session.ServerId);
        try
        {
            TerminalBridgeOpenRequest open = BuildBridgeRequest(session, server);
            if (session.TargetType == TerminalTargetType.Container && session.Mode == TerminalMode.Attach)
                await ReplayLogsAsync(session, server, cancellationToken);
            await bridge.OpenAsync(open, PublishOutputAsync, HandleExitAsync, cancellationToken);
            session.MarkConnected(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await PublishStatusAsync(session, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.Fail(redactor.Redact(ex.Message), timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(
                teamId,
                actorUserId,
                AuditActions.TerminalSessionFailed,
                new AuditTarget("terminal_session", session.Id.Value.ToString("D")),
                null,
                Metadata(session),
                cancellationToken);
            await PublishStatusAsync(session, cancellationToken);
        }

        return ToDetails(session);
    }

    /// <summary>Issues a new attach token for a live session of the caller (for example after a reconnect).</summary>
    public TerminalSessionDetails IssueAttachToken(UserId actorUserId, TeamId teamId, TerminalSessionId sessionId)
    {
        RequireSessionOwner(actorUserId, teamId, sessionId);
        TerminalSession session = LoadSession(sessionId);
        if (TerminalSession.IsTerminal(session.Status) || session.Status == TerminalSessionStatus.Closing)
            throw new InvalidOperationException("Terminal session has ended.");
        var denial = AccessDenial(actorUserId, teamId, session);
        if (denial is not null) throw new UnauthorizedAccessException(denial);
        (string token, DateTimeOffset expiresAt) = _tokens.Issue(actorUserId, teamId, sessionId);
        return ToDetails(session) with { AttachToken = token, AttachTokenExpiresAt = expiresAt };
    }

    public TerminalSessionDetails Get(UserId actorUserId, TeamId teamId, TerminalSessionId sessionId)
    {
        RequireSessionOwner(actorUserId, teamId, sessionId);
        return ToDetails(LoadSession(sessionId));
    }

    public async Task SendInputAsync(
        UserId actorUserId,
        TeamId teamId,
        TerminalSessionId sessionId,
        string data,
        CancellationToken cancellationToken = default)
    {
        TerminalSession session = await GetWritableSessionAsync(actorUserId, teamId, sessionId, cancellationToken);
        if (string.IsNullOrEmpty(data)) return;
        int inputBytes = Encoding.UTF8.GetByteCount(data);
        if (inputBytes > _policy.MaxInputBytes)
            throw new InvalidOperationException("Terminal input exceeded the configured maximum chunk size.");

        session.RecordInput(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TerminalSessionInput,
            new AuditTarget("terminal_session", session.Id.Value.ToString("D")),
            null,
            new Dictionary<string, object?> { ["bytes"] = inputBytes },
            cancellationToken);
        await bridge.SendInputAsync(sessionId, data, cancellationToken);
    }

    public async Task ResizeAsync(
        UserId actorUserId,
        TeamId teamId,
        TerminalSessionId sessionId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        TerminalSession session = await GetWritableSessionAsync(actorUserId, teamId, sessionId, cancellationToken);
        session.Resize(columns, rows, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await bridge.ResizeAsync(new TerminalBridgeResizeRequest(sessionId, columns, rows), cancellationToken);
        await PublishStatusAsync(session, cancellationToken);
    }

    public async Task CloseAsync(
        UserId actorUserId,
        TeamId teamId,
        TerminalSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireSessionOwner(actorUserId, teamId, sessionId);
        TerminalSession session = LoadSession(sessionId);
        if (TerminalSession.IsTerminal(session.Status)) return;
        _tokens.Revoke(sessionId);
        session.BeginClose(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await bridge.CloseAsync(sessionId, cancellationToken);
        session.Close(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TerminalSessionClosed,
            new AuditTarget("terminal_session", session.Id.Value.ToString("D")),
            null,
            Metadata(session),
            cancellationToken);
        await PublishStatusAsync(session, cancellationToken);
    }

    /// <summary>
    ///     Continuous re-authorization: ends every live session whose owner lost <c>terminals.open</c> or access to the
    ///     server/resource (re-evaluated against the server's own team), whose server disabled terminals, that went idle,
    ///     outlived its maximum lifetime, or was never attached. Also reconciles sessions whose process already exited.
    /// </summary>
    public async Task<int> RevalidateAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        TerminalSession[] live = dbContext.TerminalSessions
            .Where(session => session.Status == TerminalSessionStatus.Opening ||
                              session.Status == TerminalSessionStatus.Connected)
            .ToArray();
        var ended = 0;
        foreach (TerminalSession session in live)
        {
            string? reason;
            if (session.Status == TerminalSessionStatus.Opening)
                reason = now - session.StartedAt > TerminalAttachTokens.Lifetime + TimeSpan.FromSeconds(30)
                    ? NotAttachedReason
                    : null;
            else if (!bridge.IsOpen(session.Id))
                reason = string.Empty;
            else if (now - session.StartedAt > _policy.MaxLifetime)
                reason = LifetimeReason;
            else if (now - session.LastActivityAt > _policy.IdleTimeout)
                reason = IdleReason;
            else
                reason = AccessDenial(session.OwnerUserId, session.TeamId, session) is null ? null : AccessLostReason;

            if (reason is null) continue;
            ended++;
            if (reason.Length == 0)
            {
                // The process exited while no request scope was alive to record it.
                session.Close(now);
                await dbContext.SaveChangesAsync(cancellationToken);
                await PublishStatusAsync(session, cancellationToken);
                continue;
            }

            await EndAsync(session, null,
                reason == AccessLostReason ? AuditActions.TerminalSessionRevoked : AuditActions.TerminalSessionClosed,
                reason, cancellationToken);
        }

        return ended;
    }

    /// <summary>Why <paramref name="userId" /> may no longer use <paramref name="session" />, or <c>null</c>.</summary>
    private string? AccessDenial(UserId userId, TeamId teamId, TerminalSession session)
    {
        if (!dbContext.Users.Any(user => user.Id == userId)) return "The user no longer exists.";
        Server? server = dbContext.Servers.SingleOrDefault(candidate => candidate.Id == session.ServerId);
        // Re-evaluated against the team that owns the server, not the team the session was opened in.
        if (server is null || server.TeamId != teamId || session.TeamId != teamId)
            return "Server is outside the active team.";
        if (!authorization.IsAuthorized(userId, teamId, ResourceRef.Server(server.Id), VesselPermissions.TerminalsOpen))
            return $"Missing required permission '{VesselPermissions.TerminalsOpen}'.";
        if (!server.TerminalAccessEnabled) return TerminalTargetPolicy.TerminalDisabledMessage;
        if (server.IsHostLocal && !authorization.IsInstanceAdmin(userId))
            return HostLocalExecutionGuard.InstanceAdminRequiredMessage;
        return session.TargetType == TerminalTargetType.Container
            ? ResourceDenial(userId, teamId, server.Id, session.ResourceKind, session.ResourceId)
            : null;
    }

    private string? ResourceDenial(UserId userId, TeamId teamId, ServerId serverId, TerminalResourceKind? kind,
        Guid? resourceId)
    {
        if (kind is null || resourceId is null) return "Container terminals require the owning resource.";
        switch (kind.Value)
        {
            case TerminalResourceKind.Application:
            {
                var applicationId = new ApplicationId(resourceId.Value);
                if (!authorization.IsAuthorized(userId, teamId, ResourceRef.Application(applicationId),
                        VesselPermissions.TerminalsOpen))
                    return "Application is outside the active team.";
                return dbContext.Applications.Any(application =>
                    application.Id == applicationId && application.ServerId == serverId)
                    ? null
                    : "The application does not run on this server.";
            }
            case TerminalResourceKind.Database:
            {
                var databaseId = new DatabaseResourceId(resourceId.Value);
                if (!authorization.IsAuthorized(userId, teamId, ResourceRef.Database(databaseId),
                        VesselPermissions.TerminalsOpen))
                    return "Database is outside the active team.";
                return dbContext.DatabaseResources.Any(database =>
                    database.Id == databaseId && database.ServerId == serverId)
                    ? null
                    : "The database does not run on this server.";
            }
            case TerminalResourceKind.Service:
            {
                var serviceId = new ServiceResourceId(resourceId.Value);
                return dbContext.ServiceResources.Any(service =>
                    service.Id == serviceId && service.TeamId == teamId && service.ServerId == serverId)
                    ? null
                    : "Service is outside the active team.";
            }
            default:
                return "Unknown terminal resource.";
        }
    }

    /// <summary>Finds the container of a resource on its server; a requested name must be one of the resource's containers.</summary>
    private async Task<string> ResolveContainerAsync(Server server, TerminalResourceKind kind, Guid resourceId,
        string? requested, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> candidates;
        if (kind == TerminalResourceKind.Database)
        {
            var databaseId = new DatabaseResourceId(resourceId);
            DatabaseResource database = dbContext.DatabaseResources.Single(item => item.Id == databaseId);
            candidates = string.IsNullOrWhiteSpace(database.ContainerName) ? [] : [database.ContainerName];
        }
        else
        {
            var filter = kind == TerminalResourceKind.Application
                ? $"label=vessel.application={resourceId:D}"
                : $"label=com.docker.compose.project={$"vessel-svc-{resourceId:N}"[..39]}";
            candidates = await ListContainersAsync(server, filter, cancellationToken);
        }

        candidates = candidates.Where(name => ContainerNamePattern().IsMatch(name)).ToArray();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No running container was found for this resource.");
        if (string.IsNullOrWhiteSpace(requested)) return candidates[0];
        return candidates.FirstOrDefault(name => string.Equals(name, requested, StringComparison.Ordinal))
               ?? throw new UnauthorizedAccessException("The container does not belong to this resource.");
    }

    private async Task<IReadOnlyList<string>> ListContainersAsync(Server server, string filter,
        CancellationToken cancellationToken)
    {
        if (serverCommands is null)
            throw new InvalidOperationException(TerminalTargetPolicy.ContainerNotSupportedMessage);
        ServerCommandResult result = await serverCommands.RunAsync(ServerExecutionTarget.From(server),
            [RuntimeCli(server), "ps", "--filter", filter, "--format", "{{.Names}}"], ContainerLookupTimeout, null,
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not list the containers of the resource on the server.");
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private TerminalBridgeOpenRequest BuildBridgeRequest(TerminalSession session, Server server)
    {
        ServerExecutionTarget? remote = server.ConnectionType == ServerConnectionType.Ssh
            ? ServerExecutionTarget.From(server)
            : null;
        string command;
        IReadOnlyList<string>? arguments;
        if (session.TargetType == TerminalTargetType.Container)
        {
            var container = session.ContainerName ?? throw new InvalidOperationException("Container is missing.");
            command = RuntimeCli(server);
            arguments = session.Mode == TerminalMode.Attach
                ? ["attach", "--sig-proxy=false", container]
                : ["exec", "-it", container, "sh", "-c", TerminalTargetPolicy.ContainerShellScript];
        }
        else if (remote is not null)
        {
            command = "ssh";
            arguments = null;
        }
        else
        {
            command = _policy.DefaultShellCommand;
            arguments = [];
        }

        return new TerminalBridgeOpenRequest(session.Id, server.Id, session.TargetType, session.ContainerName, command,
            session.Columns, session.Rows, _policy.IdleTimeout, _policy.MaxLifetime, server.ConnectionType, arguments,
            remote);
    }

    /// <summary>Attach consoles show the last log lines first (upstream Coolify v4.6), redacted like all output.</summary>
    private async Task ReplayLogsAsync(TerminalSession session, Server server, CancellationToken cancellationToken)
    {
        if (serverCommands is null || session.ContainerName is null) return;
        try
        {
            ServerCommandResult logs = await serverCommands.RunAsync(ServerExecutionTarget.From(server),
                [RuntimeCli(server), "logs", "--tail",
                    TerminalTargetPolicy.AttachLogTailLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    session.ContainerName],
                ContainerLookupTimeout, null, cancellationToken);
            var text = (logs.StandardOutput + logs.StandardError).Replace("\n", "\r\n", StringComparison.Ordinal);
            if (text.Length > 0)
                await PublishOutputAsync(new TerminalOutputChunk(session.Id.Value, "stdout", text,
                    timeProvider.GetUtcNow()), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The console still attaches without history.
        }
    }

    private async Task EndAsync(TerminalSession session, UserId? actorUserId, string auditAction, string reason,
        CancellationToken cancellationToken)
    {
        _tokens.Revoke(session.Id);
        try
        {
            await bridge.CloseAsync(session.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The session is ended in the database regardless.
        }

        if (session.Status == TerminalSessionStatus.Opening)
            session.Cancel(timeProvider.GetUtcNow());
        else
            session.Fail(reason, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(session.TeamId, actorUserId ?? session.OwnerUserId, auditAction,
            new AuditTarget("terminal_session", session.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>(Metadata(session)) { ["reason"] = reason }, cancellationToken);
        await PublishStatusAsync(session, cancellationToken);
    }

    private void RequireSessionOwner(UserId actorUserId, TeamId teamId, TerminalSessionId sessionId)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.TerminalsOpen))
            throw new UnauthorizedAccessException($"Missing required permission '{VesselPermissions.TerminalsOpen}'.");
        if (!authorization.CanAccessTerminalSession(actorUserId, teamId, sessionId))
            throw new UnauthorizedAccessException("Terminal session is outside the active team.");
    }

    private TerminalSession LoadSession(TerminalSessionId sessionId)
    {
        return dbContext.TerminalSessions.SingleOrDefault(session => session.Id == sessionId)
               ?? throw new InvalidOperationException("Terminal session was not found.");
    }

    private async Task<TerminalSession> GetWritableSessionAsync(UserId actorUserId, TeamId teamId,
        TerminalSessionId sessionId, CancellationToken cancellationToken)
    {
        RequireSessionOwner(actorUserId, teamId, sessionId);
        TerminalSession session = LoadSession(sessionId);
        if (session.Status != TerminalSessionStatus.Connected)
            throw new InvalidOperationException("Terminal session is not connected.");
        var denial = AccessDenial(actorUserId, teamId, session);
        if (denial is not null)
        {
            await EndAsync(session, actorUserId, AuditActions.TerminalSessionRevoked, AccessLostReason,
                cancellationToken);
            throw new UnauthorizedAccessException(denial);
        }

        return session;
    }

    private async Task PublishOutputAsync(TerminalOutputChunk chunk, CancellationToken cancellationToken)
    {
        var payload = chunk with { Data = redactor.Redact(chunk.Data) };
        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Terminal, chunk.SessionId.ToString("D")),
            new RealtimeMessage("terminal.output", payload),
            cancellationToken);
    }

    private async Task HandleExitAsync(TerminalSessionId sessionId, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            TerminalSession? session = dbContext.TerminalSessions.SingleOrDefault(session => session.Id == sessionId);
            if (session is null || TerminalSession.IsTerminal(session.Status)) return;

            if (string.IsNullOrWhiteSpace(reason))
                session.Close(timeProvider.GetUtcNow());
            else
                session.Fail(redactor.Redact(reason), timeProvider.GetUtcNow());

            await dbContext.SaveChangesAsync(cancellationToken);
            await PublishStatusAsync(session, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The request scope that started the process is gone; RevalidateAsync records the exit.
        }
    }

    private Task PublishStatusAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        return realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Terminal, session.Id.Value.ToString("D")),
            new RealtimeMessage("terminal.status", ToDetails(session)),
            cancellationToken);
    }

    private static string RuntimeCli(Server server)
    {
        return server.Runtime == ContainerRuntimeKind.Podman ? "podman" : "docker";
    }

    private static string Describe(Server server, TerminalTargetType targetType, TerminalMode mode, string? container)
    {
        if (targetType == TerminalTargetType.Server)
            return server.IsHostLocal ? TerminalSecurityPolicy.Default.DefaultShellCommand : "ssh login shell";
        return mode == TerminalMode.Attach
            ? $"{RuntimeCli(server)} attach {container}"
            : $"{RuntimeCli(server)} exec -it {container} (shell)";
    }

    private static TerminalSessionDetails ToDetails(TerminalSession session)
    {
        return new TerminalSessionDetails(
            session.Id.Value,
            session.ServerId.Value,
            session.TargetType,
            session.ContainerName,
            session.Status,
            session.Columns,
            session.Rows,
            session.StartedAt,
            session.EndedAt,
            session.LastActivityAt,
            session.FailureReason,
            session.Mode);
    }

    private static Dictionary<string, object?> Metadata(TerminalSession session)
    {
        return new Dictionary<string, object?>
        {
            ["serverId"] = session.ServerId.Value,
            ["targetType"] = session.TargetType.ToString(),
            ["containerName"] = session.ContainerName,
            ["mode"] = session.Mode.ToString(),
            ["resourceKind"] = session.ResourceKind?.ToString(),
            ["resourceId"] = session.ResourceId,
            ["status"] = session.Status.ToString()
        };
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNamePattern();
}
