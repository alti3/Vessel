using System.Formats.Tar;
using Microsoft.Extensions.Options;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Application.Ssh;
using Vessel.Infrastructure.Ssh;

namespace Vessel.Infrastructure.Servers;

/// <summary>
///     Uploads a local directory to an SSH server as a tar stream on the SSH connection's standard input. The archive is
///     built here (not with a local <c>tar</c>): regular files and directories keep owner-only modes (0600/0700, 0700 for
///     executables), symbolic links are kept only when they resolve inside the source directory, and special files are
///     skipped. The remote side unpacks into a fresh directory under the owned remote root with <c>umask 077</c>.
/// </summary>
public sealed class SshServerDirectoryTransfer(
    IOptions<ServerFilesOptions> options,
    ServerCommandExecutor executor,
    ISshClient sshClient,
    SshRuntimeFiles runtimeFiles) : IServerDirectoryTransfer
{
    public const string ExtractScript =
        "set -eu; umask 077; dir=$1; rm -rf -- \"$dir\"; mkdir -p -- \"$dir\"; exec tar -xf - -C \"$dir\"";

    public const string DeleteScript = "set -eu; rm -rf -- \"$1\"";

    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(30);

    private readonly string _root = ServerFilePaths.ValidateAbsoluteRoot(options.Value.RemoteRootDirectory);

    public string ResolveHostPath(ServerExecutionTarget target, string remoteRelativePath)
    {
        EnsureRemote(target);
        return RemotePath(remoteRelativePath);
    }

    public async Task<string> UploadAsync(
        ServerExecutionTarget target,
        string localDirectory,
        string remoteRelativePath,
        CancellationToken cancellationToken = default)
    {
        EnsureRemote(target);
        if (!Directory.Exists(localDirectory))
            throw new DirectoryNotFoundException("The local directory to upload does not exist.");
        var remote = RemotePath(remoteRelativePath);

        var scratch = runtimeFiles.NewScratchDirectory();
        try
        {
            var archivePath = Path.Combine(scratch, "upload.tar");
            await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write))
            {
                await WriteArchiveAsync(localDirectory, archive, cancellationToken);
            }

            await using SshConnectionLease lease = await executor.AcquireAsync(target, null, cancellationToken);
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read);
            SshCommandResult result = await sshClient.RunCommandWithInputAsync(
                new SshCommandRequest(lease.Options, PosixShell.Join(["sh", "-c", ExtractScript, "vessel", remote]),
                    UploadTimeout),
                input,
                cancellationToken);
            await executor.ObserveAsync(target, result.StandardError);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Could not upload the deployment files to the server (exit code {result.ExitCode}): {result.StandardError.Trim()}");
            return remote;
        }
        finally
        {
            runtimeFiles.Delete(scratch);
        }
    }

    public async Task DeleteAsync(
        ServerExecutionTarget target,
        string remoteRelativePath,
        CancellationToken cancellationToken = default)
    {
        EnsureRemote(target);
        ServerCommandResult result = await executor.RunAsync(target,
            ["sh", "-c", DeleteScript, "vessel", RemotePath(remoteRelativePath)], TimeSpan.FromMinutes(5), null,
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Could not remove the deployment files from the server (exit code {result.ExitCode}).");
    }

    /// <summary>Writes <paramref name="sourceDirectory" /> as a PAX tar archive (relative entry names).</summary>
    public static async Task WriteArchiveAsync(string sourceDirectory, Stream destination,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        await using var writer = new TarWriter(destination, TarEntryFormat.Pax, true);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos()
                         .OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is { } linkTarget)
                {
                    if (!ResolvesInside(root, Path.GetDirectoryName(entry.FullName)!, linkTarget)) continue;
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, name)
                    {
                        LinkName = linkTarget,
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    }, cancellationToken);
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name + "/")
                    {
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    }, cancellationToken);
                    pending.Push(entry.FullName);
                    continue;
                }

                if (entry is not FileInfo file || (file.Attributes & FileAttributes.Device) != 0) continue;
                var executable = !OperatingSystem.IsWindows() &&
                                 (File.GetUnixFileMode(file.FullName) & UnixFileMode.UserExecute) != 0;
                await using var data = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = data,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                           (executable ? UnixFileMode.UserExecute : UnixFileMode.None),
                    ModificationTime = file.LastWriteTimeUtc
                }, cancellationToken);
            }
        }
    }

    private static bool ResolvesInside(string root, string linkDirectory, string linkTarget)
    {
        if (Path.IsPathRooted(linkTarget)) return false;
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(linkDirectory, linkTarget)));
        return string.Equals(resolved, root, StringComparison.Ordinal) ||
               resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private string RemotePath(string relativePath)
    {
        return $"{_root}/{ServerFilePaths.ValidateRelative(relativePath)}";
    }

    private static void EnsureRemote(ServerExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsRemote)
            throw new NotSupportedException("Directory transfer only serves SSH servers.");
    }
}
