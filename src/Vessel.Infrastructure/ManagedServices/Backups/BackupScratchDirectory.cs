using Vessel.Application.Files;
using Vessel.Infrastructure.Files;

namespace Vessel.Infrastructure.ManagedServices.Backups;

/// <summary>
/// An owned, per-operation temporary directory for backup and restore artifacts. Paths are validated against the
/// scratch root, files are owner-only, and disposing the directory deletes everything in it.
/// </summary>
public sealed class BackupScratchDirectory : IAsyncDisposable
{
    private readonly IPathSafetyService _paths;

    private BackupScratchDirectory(string path, IPathSafetyService paths)
    {
        DirectoryPath = path;
        _paths = paths;
    }

    public string DirectoryPath { get; }

    public static BackupScratchDirectory Create(string scratchRoot, string kind, Guid operationId,
        IPathSafetyService paths)
    {
        Directory.CreateDirectory(scratchRoot);
        var candidate = Path.Combine(scratchRoot, $"{kind}-{operationId:N}-{Guid.NewGuid():N}");
        var path = paths.EnsureOwnedPath(scratchRoot, candidate);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new BackupScratchDirectory(path, paths);
    }

    /// <summary>Creates (or truncates) an owner-only file in the scratch directory and opens it for writing.</summary>
    public FileStream CreateFile(string fileName)
    {
        var path = PathFor(fileName);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous);
        OwnerOnlyFilePermissions.Apply(path);
        return stream;
    }

    public FileStream OpenRead(string fileName)
    {
        return new FileStream(PathFor(fileName), FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public string PathFor(string fileName)
    {
        return _paths.EnsureOwnedRelativePath(DirectoryPath, fileName);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
        catch (IOException)
        {
            // Best effort: each operation uses a fresh, uniquely named directory, so a leftover is never reused.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
