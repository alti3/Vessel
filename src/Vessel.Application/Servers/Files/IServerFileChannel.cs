using Vessel.Domain.Servers;

namespace Vessel.Application.Servers.Files;

/// <summary>
///     Delivers Vessel-owned files to a server's runtime host. All paths are relative to the channel's owned root
///     directory on that host and are validated by <see cref="ServerFilePaths" />. Writes are atomic
///     (temporary file in the same directory, mode applied, then rename over the destination).
/// </summary>
public interface IServerFileChannel
{
    bool Supports(ServerConnectionType connectionType);

    /// <summary>Absolute path of <paramref name="relativePath" /> on the target host (for bind mounts).</summary>
    string ResolveHostPath(ServerExecutionTarget target, string relativePath);

    Task EnsureDirectoryAsync(
        ServerExecutionTarget target,
        string relativePath,
        UnixFileMode mode,
        CancellationToken cancellationToken = default);

    Task<ServerFileWriteResult> WriteAsync(
        ServerExecutionTarget target,
        ServerFileWrite file,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the file contents, or <see langword="null" /> when the file does not exist.</summary>
    Task<string?> ReadTextAsync(
        ServerExecutionTarget target,
        string relativePath,
        CancellationToken cancellationToken = default);
}

/// <summary>A file to deliver. With <c>CreateOnly</c> an existing destination is kept (for example ACME storage).</summary>
public sealed record ServerFileWrite(string RelativePath, string Contents, UnixFileMode Mode, bool CreateOnly = false);

public enum ServerFileWriteResult
{
    Written,
    AlreadyExists
}

public static class ServerFileModes
{
    public const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public const UnixFileMode WorldReadable = OwnerReadWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public const UnixFileMode OwnerOnlyDirectory = OwnerReadWrite | UnixFileMode.UserExecute;

    public const UnixFileMode WorldReadableDirectory = OwnerOnlyDirectory | UnixFileMode.GroupRead |
                                                       UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                                       UnixFileMode.OtherExecute;

    /// <summary>Octal representation used by <c>chmod</c> (for example <c>0644</c>).</summary>
    public static string ToOctal(UnixFileMode mode)
    {
        var value = (int)mode & 0x1FF;
        return "0" + Convert.ToString(value, 8).PadLeft(3, '0');
    }
}

public static class ServerFileChannels
{
    public static IServerFileChannel For(this IEnumerable<IServerFileChannel> channels, ServerExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(target);
        return channels.FirstOrDefault(channel => channel.Supports(target.ConnectionType))
               ?? throw new NotSupportedException(
                   $"No file channel supports servers with connection type '{target.ConnectionType}'.");
    }
}
