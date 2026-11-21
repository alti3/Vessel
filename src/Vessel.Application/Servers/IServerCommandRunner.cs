using Vessel.Application.Processes;
using Vessel.Domain;

namespace Vessel.Application.Servers;

public sealed record ServerCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
///     Runs a structured argument vector on a server's runtime host: on the control-plane host through
///     <see cref="IProcessRunner" /> for <c>Local</c>/<c>DockerSocket</c> servers, or on the remote host over SSH (every
///     argument POSIX-quoted, never interpolated) for <c>Ssh</c> servers. This is the single remote execution path for
///     runtime commands, file delivery, validation and terminals.
/// </summary>
public interface IServerCommandRunner
{
    Task<ServerCommandResult> RunAsync(
        ServerExecutionTarget target,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? standardInput = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs with an explicit team key instead of the server's key (key rotation).</summary>
    Task<ServerCommandResult> RunWithKeyAsync(
        ServerExecutionTarget target,
        SshKeyId keyId,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Runs in <paramref name="workingDirectory" /> and yields output lines as they arrive.</summary>
    IAsyncEnumerable<ProcessOutputLine> StreamAsync(
        ServerExecutionTarget target,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        IReadOnlyList<string>? secretValues = null,
        CancellationToken cancellationToken = default);
}
