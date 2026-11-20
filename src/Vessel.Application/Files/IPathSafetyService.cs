namespace Vessel.Application.Files;

public interface IPathSafetyService
{
    /// <summary>
    /// Returns the full path of <paramref name="candidatePath"/> after verifying it is inside
    /// <paramref name="rootDirectory"/> both lexically and after resolving every existing symbolic link.
    /// </summary>
    string EnsureOwnedPath(string rootDirectory, string candidatePath, bool mustAlreadyExist = false);

    /// <summary>Validates a relative path (no rooted paths or traversal segments) and applies <see cref="EnsureOwnedPath"/>.</summary>
    string EnsureOwnedRelativePath(string rootDirectory, string relativePath);

    /// <summary>
    /// Like <see cref="EnsureOwnedRelativePath"/> but additionally rejects any existing symbolic link on the path
    /// below the root (intermediate directories and the final component). Use for untrusted trees such as
    /// cloned repositories and for files Vessel writes.
    /// </summary>
    string EnsureNoSymlinks(string rootDirectory, string relativePath);
}
