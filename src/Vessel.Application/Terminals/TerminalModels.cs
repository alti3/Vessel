using Vessel.Domain;
using Vessel.Domain.Terminals;

namespace Vessel.Application.Terminals;

public sealed record OpenTerminalSessionRequest(
    Guid ServerId,
    TerminalTargetType TargetType = TerminalTargetType.Server,
    string? ContainerName = null,
    int Columns = 120,
    int Rows = 32);

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
    string? FailureReason);

public sealed record TerminalInputRequest(Guid SessionId, string Data);

public sealed record TerminalResizeRequest(Guid SessionId, int Columns, int Rows);

public sealed record TerminalOutputChunk(Guid SessionId, string Stream, string Data, DateTimeOffset CreatedAt);

public sealed record TerminalBridgeOpenRequest(
    TerminalSessionId SessionId,
    ServerId ServerId,
    TerminalTargetType TargetType,
    string? ContainerName,
    string Command,
    int Columns,
    int Rows,
    TimeSpan IdleTimeout,
    TimeSpan MaxLifetime);

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

public interface ITerminalProcessBridge
{
    Task OpenAsync(
        TerminalBridgeOpenRequest request,
        Func<TerminalOutputChunk, CancellationToken, Task> onOutput,
        Func<TerminalSessionId, string?, CancellationToken, Task> onExit,
        CancellationToken cancellationToken = default);

    Task SendInputAsync(TerminalSessionId sessionId, string data, CancellationToken cancellationToken = default);

    Task ResizeAsync(TerminalBridgeResizeRequest request, CancellationToken cancellationToken = default);

    Task CloseAsync(TerminalSessionId sessionId, CancellationToken cancellationToken = default);
}
