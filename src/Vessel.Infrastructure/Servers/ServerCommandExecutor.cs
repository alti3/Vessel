using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Application.Ssh;
using Vessel.Domain;

namespace Vessel.Infrastructure.Servers;

/// <summary>
///     Runs a structured argument vector on a server's runtime host: locally through <see cref="IProcessRunner" />,
///     or remotely through <see cref="ISshClient" /> with every argument POSIX-quoted. Remote connections are resolved
///     by <see cref="IServerSshConnectionFactory" /> (team key, pinned host key, fixed SSH options); after each remote
///     command a reported host key change is recorded and the team notified.
/// </summary>
public sealed partial class ServerCommandExecutor(
    IProcessRunner processRunner,
    ISshClient sshClient,
    IServerSshConnectionFactory? connections = null) : IServerCommandRunner
{
    public const string DefaultSshUser = "root";

    /// <summary>Changes into the directory given as <c>$1</c>, then executes the remaining arguments.</summary>
    public const string WorkingDirectoryScript = "cd -- \"$1\" || exit 1; shift; exec \"$@\"";

    public async Task<ServerCommandResult> RunAsync(
        ServerExecutionTarget target,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateArguments(arguments);

        if (target.IsRemote)
            return await RunRemoteAsync(target, null, arguments, timeout, standardInput, cancellationToken);

        var command = new ProcessCommand(arguments[0], arguments.Skip(1).ToArray(), Timeout: timeout);
        ProcessResult result;
        if (standardInput is null)
        {
            result = await processRunner.RunTextAsync(command, cancellationToken);
        }
        else
        {
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(standardInput), false);
            result = await processRunner.RunTextWithInputAsync(command, input, cancellationToken);
        }

        var exitCode = result.Succeeded ? 0 : result.ExitInfo.ExitCode == 0 ? -1 : result.ExitInfo.ExitCode;
        return new ServerCommandResult(exitCode, result.StandardOutput, result.StandardError);
    }

    public Task<ServerCommandResult> RunWithKeyAsync(
        ServerExecutionTarget target,
        SshKeyId keyId,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateArguments(arguments);
        if (!target.IsRemote)
            throw new InvalidOperationException("An explicit SSH key only applies to SSH servers.");
        return RunRemoteAsync(target, keyId, arguments, timeout, null, cancellationToken);
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamAsync(
        ServerExecutionTarget target,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        IReadOnlyList<string>? secretValues = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateArguments(arguments);

        if (!target.IsRemote)
        {
            await foreach (ProcessOutputLine line in processRunner.StreamLinesAsync(new ProcessCommand(
                                   arguments[0],
                                   arguments.Skip(1).ToArray(),
                                   workingDirectory,
                                   Timeout: timeout,
                                   OutputMode: ProcessOutputMode.Lines,
                                   Redaction: new ProcessRedactionProfile(secretValues ?? [], [])),
                               cancellationToken))
                yield return line;
            yield break;
        }

        await using SshConnectionLease lease = await AcquireAsync(target, null, cancellationToken);
        var request = new SshCommandRequest(lease.Options, PosixShell.Join(WithWorkingDirectory(arguments, workingDirectory)),
            timeout, secretValues);
        var standardError = new StringBuilder();
        await foreach (ProcessOutputLine line in sshClient.StreamCommandAsync(request, cancellationToken))
        {
            if (line.Stream == ProcessStreamKind.StandardError && standardError.Length < 8192)
                standardError.AppendLine(line.Content);
            yield return line;
        }

        await ObserveAsync(target, standardError.ToString());
    }

    /// <summary>
    ///     Builds SSH options for a server without a connection factory (legacy path: service user's identity and
    ///     known_hosts). Host and user are validated so neither can be read by <c>ssh</c> as an option (for example a user
    ///     of <c>-oProxyCommand=...</c>). Host keys must already be known.
    /// </summary>
    public static SshConnectionOptions SshConnectionFor(ServerExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!HostPattern().IsMatch(target.Host))
            throw new InvalidOperationException("Server host is not a valid SSH host name or address.");
        var user = string.IsNullOrWhiteSpace(target.User) ? DefaultSshUser : target.User;
        if (!UserPattern().IsMatch(user))
            throw new InvalidOperationException("Server SSH user is invalid.");
        if (target.Port is < 1 or > 65535)
            throw new InvalidOperationException("Server SSH port is invalid.");

        return new SshConnectionOptions(target.Host, target.Port, user,
            HostIdentityPolicy: SshHostIdentityPolicy.RequireKnownHost,
            ConnectTimeout: TimeSpan.FromSeconds(15));
    }

    /// <summary>Wraps an argument vector so it runs in <paramref name="workingDirectory" /> (no shell interpolation).</summary>
    public static IReadOnlyList<string> WithWorkingDirectory(IReadOnlyList<string> arguments, string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return arguments;
        if (!workingDirectory.StartsWith('/'))
            throw new ArgumentException("Remote working directories must be absolute.", nameof(workingDirectory));
        List<string> wrapped = ["sh", "-c", WorkingDirectoryScript, "vessel", workingDirectory];
        wrapped.AddRange(arguments);
        return wrapped;
    }

    /// <summary>Resolves the SSH connection of a server (factory when configured, legacy options otherwise).</summary>
    public async Task<SshConnectionLease> AcquireAsync(ServerExecutionTarget target, SshKeyId? keyId,
        CancellationToken cancellationToken)
    {
        if (connections is not null)
            return await connections.AcquireAsync(target, keyId, cancellationToken);
        if (keyId.HasValue)
            throw new InvalidOperationException("SSH key selection requires the SSH connection factory.");
        return new SshConnectionLease(SshConnectionFor(target));
    }

    public Task ObserveAsync(ServerExecutionTarget target, string? standardError)
    {
        return connections?.ObserveAsync(target, standardError, CancellationToken.None) ?? Task.CompletedTask;
    }

    private async Task<ServerCommandResult> RunRemoteAsync(
        ServerExecutionTarget target,
        SshKeyId? keyId,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        // Validates host and user before any connection data is resolved.
        SshConnectionFor(target);
        await using SshConnectionLease lease = await AcquireAsync(target, keyId, cancellationToken);
        var request = new SshCommandRequest(lease.Options, PosixShell.Join(arguments), timeout);
        SshCommandResult remote;
        if (standardInput is null)
        {
            remote = await sshClient.RunCommandAsync(request, cancellationToken);
        }
        else
        {
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(standardInput), false);
            remote = await sshClient.RunCommandWithInputAsync(request, input, cancellationToken);
        }

        await ObserveAsync(target, remote.StandardError);
        return new ServerCommandResult(remote.ExitCode, remote.StandardOutput, remote.StandardError);
    }

    private static void ValidateArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            throw new ArgumentException("A command is required.", nameof(arguments));
    }

    [GeneratedRegex("^[A-Za-z0-9_:\\[][A-Za-z0-9._:%\\[\\]-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();
}
