using Vessel.Application.Files;

namespace Vessel.Infrastructure.Files;

/// <summary>
/// Owned-path validation that resolves real paths. Lexical containment alone is not enough: a symbolic link
/// inside an owned root (for example committed to a cloned repository) can point anywhere on the host, so
/// every existing path component is resolved and the result must stay inside the real owned root.
/// </summary>
public sealed class PathSafetyService : IPathSafetyService
{
    private const int MaxLinkDepth = 40;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string EnsureOwnedPath(string rootDirectory, string candidatePath, bool mustAlreadyExist = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = NormalizeRoot(rootDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        if (!IsWithinRoot(root, candidate))
            throw new InvalidOperationException("Path is outside the owned root directory.");

        // Resolve symbolic links in every existing component (including intermediate directories) and require
        // the real location to remain inside the real root.
        var realRoot = EnsureTrailingSeparator(ResolveRealPath(root));
        var realCandidate = ResolveRealPath(candidatePath);
        if (!IsWithinRoot(realRoot, realCandidate))
            throw new InvalidOperationException("Path resolves outside the owned root directory through a symbolic link.");

        if (mustAlreadyExist && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new DirectoryNotFoundException("Path does not exist inside the owned root directory.");

        return candidate;
    }

    public string EnsureOwnedRelativePath(string rootDirectory, string relativePath)
    {
        string[] segments = ValidateRelativeSegments(relativePath);
        var root = NormalizeRoot(rootDirectory);
        return EnsureOwnedPath(root, Path.Combine([root, .. segments]));
    }

    public string EnsureNoSymlinks(string rootDirectory, string relativePath)
    {
        string[] segments = ValidateRelativeSegments(relativePath);
        var root = NormalizeRoot(rootDirectory);
        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
                throw new InvalidOperationException("Path must not contain symbolic links inside the owned root directory.");
        }

        return EnsureOwnedPath(root, current);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> the way the kernel does: every existing symbolic link (intermediate or
    /// final) is followed and <c>..</c> is applied to the already-resolved parent (physical, not lexical).
    /// Components that do not exist are appended as-is.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var absolute = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
        var pathRoot = Path.GetPathRoot(absolute)!;
        var pending = new LinkedList<string>(SplitSegments(absolute[pathRoot.Length..]));
        var current = pathRoot;
        var linksFollowed = 0;

        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var segment = node.Value;
            if (segment == ".") continue;
            if (segment == "..")
            {
                current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar)) ?? pathRoot;
                if (current.Length == 0) current = pathRoot;
                continue;
            }

            var next = Path.Combine(current, segment);
            string? linkTarget = new FileInfo(next).LinkTarget;
            if (linkTarget is null)
            {
                current = next;
                continue;
            }

            if (++linksFollowed > MaxLinkDepth)
                throw new InvalidOperationException("Too many levels of symbolic links.");

            string[] targetSegments;
            if (Path.IsPathRooted(linkTarget))
            {
                var targetRoot = Path.GetPathRoot(linkTarget)!;
                current = targetRoot;
                targetSegments = SplitSegments(linkTarget[targetRoot.Length..]);
            }
            else
            {
                targetSegments = SplitSegments(linkTarget);
            }

            for (var index = targetSegments.Length - 1; index >= 0; index--)
                pending.AddFirst(targetSegments[index]);
        }

        return current;
    }

    private static string[] SplitSegments(string path)
    {
        return path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    internal static bool IsWithinRoot(string root, string candidate)
    {
        var normalizedRoot = EnsureTrailingSeparator(root);
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, PathComparison)
               || string.Equals(EnsureTrailingSeparator(normalizedCandidate), normalizedRoot, PathComparison);
    }

    private static bool IsLink(string path)
    {
        return new FileInfo(path).LinkTarget is not null;
    }

    private static string[] ValidateRelativeSegments(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (IsRootedPath(relativePath))
            throw new InvalidOperationException("Path must be relative to the owned root directory.");

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            throw new InvalidOperationException("Path must name a file or directory inside the owned root directory.");
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidOperationException("Path must not contain traversal segments.");
        if (segments.Any(segment => segment.Contains('\0', StringComparison.Ordinal)))
            throw new InvalidOperationException("Path must not contain null characters.");

        return segments;
    }

    private static bool IsRootedPath(string path)
    {
        return Path.IsPathRooted(path)
               || path.StartsWith('\\')
               || (path.Length >= 3
                   && char.IsAsciiLetter(path[0])
                   && path[1] == ':'
                   && (path[2] == '\\' || path[2] == '/'));
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        return EnsureTrailingSeparator(Path.GetFullPath(rootDirectory));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}
