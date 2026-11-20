using System.Text;
using System.Text.RegularExpressions;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Application.Ssh;

namespace Vessel.Infrastructure.Servers;

public sealed record ServerCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
///     Runs a structured argument vector on a server's runtime host: locally through <see cref="IProcessRunner" />,
///     or remotely through <see cref="ISshClient" /> with every argument POSIX-quoted.
/// </summary>
public sealed partial class ServerCommandExecutor(IProcessRunner processRunner, ISshClient sshClient)
{
    public const string DefaultSshUser = "root";

    public async Task<ServerCommandResult> RunAsync(
        ServerExecutionTarget target,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            throw new ArgumentException("A command is required.", nameof(arguments));

        if (target.IsRemote)
        {
            var request = new SshCommandRequest(SshConnectionFor(target), PosixShell.Join(arguments), timeout);
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

            return new ServerCommandResult(remote.ExitCode, remote.StandardOutput, remote.StandardError);
        }

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

    /// <summary>
    ///     Builds SSH options for a server. Host and user are validated so neither can be read by <c>ssh</c> as an option
    ///     (for example a user of <c>-oProxyCommand=...</c>). Host keys must already be known.
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

    [GeneratedRegex("^[A-Za-z0-9_:\\[][A-Za-z0-9._:%\\[\\]-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();
}
