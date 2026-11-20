using Microsoft.Extensions.Options;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Domain.Servers;

namespace Vessel.Infrastructure.Servers;

/// <summary>
///     File channel for SSH servers, built on <see cref="ServerCommandExecutor" /> (OpenSSH via <c>IProcessRunner</c>).
///     Each operation runs a fixed POSIX <c>sh</c> script; paths and modes are passed as positional arguments, never
///     interpolated into the script. Contents are streamed over standard input into a temporary file in the
///     destination directory under <c>umask 077</c>, then <c>chmod</c>-ed and renamed over the destination.
/// </summary>
public sealed class SshServerFileChannel(IOptions<ServerFilesOptions> options, ServerCommandExecutor executor)
    : IServerFileChannel
{
    public const string WriteScript =
        "set -eu; dir=$1; tmp=$2; dst=$3; mode=$4; create_only=$5; " +
        "if [ \"$create_only\" = 1 ] && [ -e \"$dst\" ]; then cat >/dev/null; echo vessel:exists; exit 0; fi; " +
        "umask 077; mkdir -p \"$dir\"; " +
        "if ! cat >\"$tmp\"; then rm -f \"$tmp\"; exit 1; fi; " +
        "chmod \"$mode\" \"$tmp\"; " +
        "if [ \"$create_only\" = 1 ]; then if ! ln \"$tmp\" \"$dst\" 2>/dev/null; then rm -f \"$tmp\"; echo vessel:exists; exit 0; fi; rm -f \"$tmp\"; " +
        "else mv -f \"$tmp\" \"$dst\"; fi; echo vessel:written";

    public const string ReadScript = "if [ -f \"$1\" ]; then exec cat \"$1\"; else exit 3; fi";

    public const string EnsureDirectoryScript = "set -eu; umask 077; mkdir -p \"$1\"; chmod \"$2\" \"$1\"";

    private const int MissingFileExitCode = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly string _root = ServerFilePaths.ValidateAbsoluteRoot(options.Value.RemoteRootDirectory);

    public bool Supports(ServerConnectionType connectionType)
    {
        return connectionType == ServerConnectionType.Ssh;
    }

    public string ResolveHostPath(ServerExecutionTarget target, string relativePath)
    {
        EnsureSupported(target);
        return $"{_root}/{ServerFilePaths.ValidateRelative(relativePath)}";
    }

    public async Task EnsureDirectoryAsync(
        ServerExecutionTarget target,
        string relativePath,
        UnixFileMode mode,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveHostPath(target, relativePath);
        ServerCommandResult result = await executor.RunAsync(target,
            ["sh", "-c", EnsureDirectoryScript, "vessel", path, ServerFileModes.ToOctal(mode)], Timeout, null,
            cancellationToken);
        ThrowIfFailed(result, $"create directory '{relativePath}'");
    }

    public async Task<ServerFileWriteResult> WriteAsync(
        ServerExecutionTarget target,
        ServerFileWrite file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var destination = ResolveHostPath(target, file.RelativePath);
        var directory = destination[..destination.LastIndexOf('/')];
        var name = destination[(destination.LastIndexOf('/') + 1)..];
        var temporary = $"{directory}/.{name}.{Guid.NewGuid():N}.tmp";

        ServerCommandResult result = await executor.RunAsync(target,
            [
                "sh", "-c", WriteScript, "vessel", directory, temporary, destination,
                ServerFileModes.ToOctal(file.Mode), file.CreateOnly ? "1" : "0"
            ],
            Timeout,
            file.Contents,
            cancellationToken);
        ThrowIfFailed(result, $"write '{file.RelativePath}'");
        return result.StandardOutput.Contains("vessel:exists", StringComparison.Ordinal)
            ? ServerFileWriteResult.AlreadyExists
            : ServerFileWriteResult.Written;
    }

    public async Task<string?> ReadTextAsync(
        ServerExecutionTarget target,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveHostPath(target, relativePath);
        ServerCommandResult result = await executor.RunAsync(target, ["sh", "-c", ReadScript, "vessel", path],
            Timeout, null, cancellationToken);
        if (result.ExitCode == MissingFileExitCode)
            return null;
        ThrowIfFailed(result, $"read '{relativePath}'");
        return result.StandardOutput;
    }

    private void EnsureSupported(ServerExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Supports(target.ConnectionType))
            throw new NotSupportedException(
                $"The SSH file channel does not serve servers with connection type '{target.ConnectionType}'.");
    }

    private static void ThrowIfFailed(ServerCommandResult result, string operation)
    {
        if (result.Succeeded)
            return;
        var detail = result.StandardError.Trim();
        throw new InvalidOperationException(detail.Length == 0
            ? $"Could not {operation} on the server (exit code {result.ExitCode})."
            : $"Could not {operation} on the server (exit code {result.ExitCode}): {detail}");
    }
}
