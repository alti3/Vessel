using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Application.Security;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Servers;
using Vessel.Domain.Terminals;

namespace Vessel.Application.Terminals;

public sealed class TerminalSessionManager(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    ITerminalProcessBridge bridge,
    IRealtimeNotifier realtime,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider)
{
    private readonly TerminalSecurityPolicy _policy = TerminalSecurityPolicy.Default;

    public async Task<TerminalSessionDetails> OpenAsync(
        UserId actorUserId,
        TeamId teamId,
        OpenTerminalSessionRequest request,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var serverId = new ServerId(request.ServerId);
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.TerminalsOpen))
            throw new UnauthorizedAccessException($"Missing required permission '{VesselPermissions.TerminalsOpen}'.");
        if (!authorization.CanAccessServer(actorUserId, serverId))
            throw new UnauthorizedAccessException("Server is outside the active team.");

        Server server = dbContext.Servers.SingleOrDefault(server => server.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        if (server.TeamId != teamId)
            throw new UnauthorizedAccessException("Server is outside the active team.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        string command = BuildCommand(request.TargetType, request.ContainerName);
        TerminalSession session = TerminalSession.Open(
            teamId,
            actorUserId,
            serverId,
            request.TargetType,
            request.ContainerName,
            command,
            request.Columns,
            request.Rows,
            now);

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

        try
        {
            await bridge.OpenAsync(
                new TerminalBridgeOpenRequest(
                    session.Id,
                    serverId,
                    request.TargetType,
                    request.ContainerName,
                    command,
                    request.Columns,
                    request.Rows,
                    _policy.IdleTimeout,
                    _policy.MaxLifetime),
                PublishOutputAsync,
                HandleExitAsync,
                cancellationToken);
            session.MarkConnected(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await PublishStatusAsync(session, cancellationToken);
        }
        catch (Exception ex)
        {
            session.Fail(redactor.Redact(ex.Message), timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.RecordAsync(
                teamId,
                actorUserId,
                AuditActions.TerminalSessionFailed,
                new AuditTarget("terminal_session", session.Id.Value.ToString("D")),
                correlationId,
                Metadata(session),
                cancellationToken);
            await PublishStatusAsync(session, cancellationToken);
        }

        return ToDetails(session);
    }

    public TerminalSessionDetails Get(UserId actorUserId, TeamId teamId, TerminalSessionId sessionId)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.TerminalsOpen))
            throw new UnauthorizedAccessException($"Missing required permission '{VesselPermissions.TerminalsOpen}'.");
        if (!authorization.CanAccessTerminalSession(actorUserId, sessionId))
            throw new UnauthorizedAccessException("Terminal session is outside the active team.");

        TerminalSession session = dbContext.TerminalSessions.SingleOrDefault(session => session.Id == sessionId)
                                  ?? throw new InvalidOperationException("Terminal session was not found.");
        return ToDetails(session);
    }

    public async Task SendInputAsync(
        UserId actorUserId,
        TeamId teamId,
        TerminalSessionId sessionId,
        string data,
        CancellationToken cancellationToken = default)
    {
        TerminalSession session = GetWritableSession(actorUserId, teamId, sessionId);
        if (string.IsNullOrEmpty(data)) return;
        if (data.Length > _policy.MaxInputBytes)
            throw new InvalidOperationException("Terminal input exceeded the configured maximum chunk size.");

        session.RecordInput(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(
            teamId,
            actorUserId,
            AuditActions.TerminalSessionInput,
            new AuditTarget("terminal_session", session.Id.Value.ToString("D")),
            null,
            new Dictionary<string, object?> { ["bytes"] = data.Length },
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
        TerminalSession session = GetWritableSession(actorUserId, teamId, sessionId);
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
        TerminalSession session = GetWritableSession(actorUserId, teamId, sessionId);
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

    private TerminalSession GetWritableSession(UserId actorUserId, TeamId teamId, TerminalSessionId sessionId)
    {
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.TerminalsOpen))
            throw new UnauthorizedAccessException($"Missing required permission '{VesselPermissions.TerminalsOpen}'.");
        if (!authorization.CanAccessTerminalSession(actorUserId, sessionId))
            throw new UnauthorizedAccessException("Terminal session is outside the active team.");

        TerminalSession session = dbContext.TerminalSessions.SingleOrDefault(session => session.Id == sessionId)
                                  ?? throw new InvalidOperationException("Terminal session was not found.");
        if (TerminalSession.IsTerminal(session.Status))
            throw new InvalidOperationException("Terminal session has ended.");
        return session;
    }

    private static string BuildCommand(TerminalTargetType targetType, string? containerName)
    {
        if (targetType == TerminalTargetType.Container)
            return $"docker exec -it {containerName} sh";

        return TerminalSecurityPolicy.Default.DefaultShellCommand;
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
        TerminalSession? session = dbContext.TerminalSessions.SingleOrDefault(session => session.Id == sessionId);
        if (session is null || TerminalSession.IsTerminal(session.Status)) return;

        if (string.IsNullOrWhiteSpace(reason))
            session.Close(timeProvider.GetUtcNow());
        else
            session.Fail(redactor.Redact(reason), timeProvider.GetUtcNow());

        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishStatusAsync(session, cancellationToken);
    }

    private Task PublishStatusAsync(TerminalSession session, CancellationToken cancellationToken)
    {
        return realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Terminal, session.Id.Value.ToString("D")),
            new RealtimeMessage("terminal.status", ToDetails(session)),
            cancellationToken);
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
            session.FailureReason);
    }

    private static Dictionary<string, object?> Metadata(TerminalSession session)
    {
        return new Dictionary<string, object?>
        {
            ["serverId"] = session.ServerId.Value,
            ["targetType"] = session.TargetType.ToString(),
            ["containerName"] = session.ContainerName,
            ["status"] = session.Status.ToString()
        };
    }
}
