namespace Vessel.Application.Servers.Files;

/// <summary>
///     Mirrors a local working directory (a deployment workspace) to an owned directory on an SSH server and removes it
///     again. Paths on the server are relative to the channel's owned remote root and validated by
///     <see cref="ServerFilePaths" />. Contents travel as a tar stream over the SSH connection's standard input and are
///     unpacked under <c>umask 077</c>; symbolic links that resolve outside the source directory are not transferred.
/// </summary>
public interface IServerDirectoryTransfer
{
    /// <summary>Absolute path on the server of <paramref name="remoteRelativePath" /> (validated, under the owned root).</summary>
    string ResolveHostPath(ServerExecutionTarget target, string remoteRelativePath);

    /// <summary>Uploads <paramref name="localDirectory" /> and returns the absolute directory path on the server.</summary>
    Task<string> UploadAsync(
        ServerExecutionTarget target,
        string localDirectory,
        string remoteRelativePath,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ServerExecutionTarget target,
        string remoteRelativePath,
        CancellationToken cancellationToken = default);
}
