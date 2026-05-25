using Vessel.Application.Files;

namespace Vessel.Infrastructure.Files;

public sealed class PathSafetyService : IPathSafetyService
{
    public string EnsureOwnedPath(string rootDirectory, string candidatePath, bool mustAlreadyExist = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = NormalizeRoot(rootDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        if (!IsWithinRoot(root, candidate))
            throw new InvalidOperationException("Path is outside the owned root directory.");

        if (mustAlreadyExist && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new DirectoryNotFoundException("Path does not exist inside the owned root directory.");

        return candidate;
    }

    public string EnsureOwnedRelativePath(string rootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Path must be relative to the owned root directory.");

        var root = NormalizeRoot(rootDirectory);
        return EnsureOwnedPath(root, Path.Combine(root, relativePath));
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        return root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var normalizedCandidate = File.Exists(candidate) || Directory.Exists(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(candidate);

        return normalizedCandidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
