using System.Text;
using Microsoft.Extensions.Options;
using Vessel.Application.Files;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Domain.Servers;

namespace Vessel.Infrastructure.Servers;

/// <summary>
///     File channel for servers whose runtime is the control-plane host. Writes go to a temporary file in the
///     destination directory created with the final mode (no world-readable window), then are renamed over the target.
/// </summary>
public sealed class LocalServerFileChannel(IOptions<ServerFilesOptions> options, IPathSafetyService pathSafety)
    : IServerFileChannel
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _root = options.Value.ResolveLocalRoot();

    public bool Supports(ServerConnectionType connectionType)
    {
        return connectionType is ServerConnectionType.Local or ServerConnectionType.DockerSocket;
    }

    public string ResolveHostPath(ServerExecutionTarget target, string relativePath)
    {
        EnsureSupported(target);
        return pathSafety.EnsureOwnedRelativePath(_root, ServerFilePaths.ValidateRelative(relativePath));
    }

    public Task EnsureDirectoryAsync(
        ServerExecutionTarget target,
        string relativePath,
        UnixFileMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveHostPath(target, relativePath);
        CreateDirectory(_root, ServerFileModes.WorldReadableDirectory);
        CreateDirectory(path, mode);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
        return Task.CompletedTask;
    }

    public async Task<ServerFileWriteResult> WriteAsync(
        ServerExecutionTarget target,
        ServerFileWrite file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var destination = ResolveHostPath(target, file.RelativePath);
        if (file.CreateOnly && File.Exists(destination))
            return ServerFileWriteResult.AlreadyExists;

        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("Destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = file.Mode;
            await using (var stream = new FileStream(temporary, streamOptions))
            {
                var bytes = Utf8NoBom.GetBytes(file.Contents);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, file.Mode);
            if (file.CreateOnly)
            {
                // Never replace an existing file (for example ACME storage holding issued certificates).
                try
                {
                    File.Move(temporary, destination, false);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    return ServerFileWriteResult.AlreadyExists;
                }
            }
            else
            {
                File.Move(temporary, destination, true);
            }

            return ServerFileWriteResult.Written;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task<string?> ReadTextAsync(
        ServerExecutionTarget target,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveHostPath(target, relativePath);
        if (!File.Exists(path))
            return null;
        return await File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(
        ServerExecutionTarget target,
        string relativeDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveHostPath(target, relativeDirectory);
        if (!Directory.Exists(path))
            return Task.FromResult<IReadOnlyList<string>>([]);
        IEnumerable<string> names = new DirectoryInfo(path).EnumerateFiles()
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(file => file.Name);
        return Task.FromResult(ServerFileNames.Filter(names));
    }

    public Task<bool> DeleteFileAsync(
        ServerExecutionTarget target,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveHostPath(target, relativePath);
        if (Directory.Exists(path))
            throw new InvalidOperationException($"'{relativePath}' is a directory.");
        var info = new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null)
            return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    private void EnsureSupported(ServerExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Supports(target.ConnectionType))
            throw new NotSupportedException(
                $"The local file channel does not serve servers with connection type '{target.ConnectionType}'.");
    }

    private static void CreateDirectory(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, mode);
    }
}
