using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Servers;
using Vessel.Domain.Terminals;

namespace Vessel.Application.Terminals;

/// <summary>
///     Opens a terminal. The browser names only targets: a server, or for container terminals the owning resource
///     (<see cref="ResourceKind" />/<see cref="ResourceId" />) and optionally one of its containers. The command line is
///     always derived server-side.
/// </summary>
public sealed record OpenTerminalSessionRequest(
    Guid ServerId,
    TerminalTargetType TargetType = TerminalTargetType.Server,
    string? ContainerName = null,
    int Columns = 120,
    int Rows = 32,
    TerminalResourceKind? ResourceKind = null,
    Guid? ResourceId = null,
    TerminalMode Mode = TerminalMode.Shell);

/// <summary>
///     A terminal session. <c>AttachToken</c> is returned only when a session is opened (or a token re-issued): a
///     single-use token, valid for <see cref="TerminalAttachTokens.Lifetime" />, bound to the user, team and session.
///     The terminal process starts only when the token is redeemed.
/// </summary>
public sealed record TerminalSessionDetails(
    Guid Id,
    Guid ServerId,
    TerminalTargetType TargetType,
    string? ContainerName,
    TerminalSessionStatus Status,
    int Columns,
    int Rows,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset LastActivityAt,
    string? FailureReason,
    TerminalMode Mode = TerminalMode.Shell,
    string? AttachToken = null,
    DateTimeOffset? AttachTokenExpiresAt = null);

public sealed record TerminalInputRequest(Guid SessionId, string Data);

public sealed record TerminalResizeRequest(Guid SessionId, int Columns, int Rows);

public sealed record TerminalAttachRequest(string Token);

public sealed record TerminalOutputChunk(Guid SessionId, string Stream, string Data, DateTimeOffset CreatedAt);

/// <summary>
///     What the bridge runs. <c>Command</c> is the executable (locally) or the first word of the remote command (SSH);
///     <c>Arguments</c> are the remaining structured arguments. <c>Remote</c> is set for SSH servers: the bridge runs the
///     command over <c>ssh -tt</c> with the server's connection, and with no arguments opens the SSH user's login shell.
/// </summary>
public sealed record TerminalBridgeOpenRequest(
    TerminalSessionId SessionId,
    ServerId ServerId,
    TerminalTargetType TargetType,
    string? ContainerName,
    string Command,
    int Columns,
    int Rows,
    TimeSpan IdleTimeout,
    TimeSpan MaxLifetime,
    ServerConnectionType TargetConnection,
    IReadOnlyList<string>? Arguments = null,
    ServerExecutionTarget? Remote = null);

public sealed record TerminalBridgeResizeRequest(TerminalSessionId SessionId, int Columns, int Rows);

public sealed record TerminalSecurityPolicy(
    TimeSpan IdleTimeout,
    TimeSpan MaxLifetime,
    int MaxInputBytes,
    string DefaultShellCommand)
{
    public static TerminalSecurityPolicy Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(2),
        8 * 1024,
        OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh");
}

public static class TerminalTargetPolicy
{
    public const string RemoteNotSupportedMessage =
        "Terminal sessions for SSH servers are not available on this Vessel instance. Vessel never substitutes a control-plane host shell for a remote server.";

    public const string ContainerNotSupportedMessage =
        "Container terminal sessions are not available on this Vessel instance; they require the PTY terminal bridge.";

    public const string TerminalDisabledMessage = "Terminal access is disabled for this server.";

    /// <summary>
    ///     Shell selection inside a container (upstream Coolify falls back when a shell is missing): bash, then ash, then
    ///     sh. A constant script, so the container name is the only variable part of the command, as its own argument.
    /// </summary>
    public const string ContainerShellScript =
        "if [ -x /bin/bash ]; then exec /bin/bash; elif [ -x /bin/ash ]; then exec /bin/ash; else exec /bin/sh; fi";

    public const int AttachLogTailLines = 200;
}

public interface ITerminalProcessBridge
{
    /// <summary>True when the bridge can run container exec/attach sessions and SSH server sessions (PTY bridge).</summary>
    bool SupportsRemoteAndContainerSessions => false;

    /// <summary>True while the bridge on this node runs a process for <paramref name="sessionId" />.</summary>
    bool IsOpen(TerminalSessionId sessionId) => true;

    Task OpenAsync(
        TerminalBridgeOpenRequest request,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
        CancellationToken cancellationToken = default);

    Task SendInputAsync(TerminalSessionId sessionId, string data, CancellationToken cancellationToken = default);

    Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default);

    Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default);
}
