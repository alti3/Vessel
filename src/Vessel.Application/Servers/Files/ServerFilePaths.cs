using System.Text.RegularExpressions;

namespace Vessel.Application.Servers.Files;

/// <summary>
///     Validation for paths that server file channels accept. Only relative paths made of conservative segments are
///     allowed, so a path can never escape the owned root, carry shell metacharacters, or be read as a command option.
/// </summary>
public static partial class ServerFilePaths
{
    public const int MaxLength = 512;

    /// <summary>Returns the normalized ('/'-separated) relative path or throws for anything unsafe.</summary>
    public static string ValidateRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Server file path is required.", nameof(relativePath));
        if (relativePath.Length > MaxLength)
            throw new ArgumentException("Server file path is too long.", nameof(relativePath));
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\') || relativePath.Contains('\\') ||
            relativePath.Contains(':'))
            throw new ArgumentException("Server file path must be relative to the owned root.", nameof(relativePath));

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new ArgumentException("Server file path is required.", nameof(relativePath));
        foreach (var segment in segments)
            if (segment is "." or ".." || !SegmentPattern().IsMatch(segment))
                throw new ArgumentException($"Server file path segment '{segment}' is not allowed.",
                    nameof(relativePath));

        return string.Join('/', segments);
    }

    /// <summary>
    ///     Validates an operator-configured absolute POSIX root directory used on remote hosts. It always starts with
    ///     '/', so it can never be read as a command option, and it must not contain traversal segments.
    /// </summary>
    public static string ValidateAbsoluteRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !rootDirectory.StartsWith('/'))
            throw new ArgumentException("Server file root must be an absolute POSIX path.", nameof(rootDirectory));
        var segments = rootDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new ArgumentException("Server file root must not be '/'.", nameof(rootDirectory));
        foreach (var segment in segments)
            if (segment is "." or ".." || !RootSegmentPattern().IsMatch(segment))
                throw new ArgumentException($"Server file root segment '{segment}' is not allowed.",
                    nameof(rootDirectory));
        return "/" + string.Join('/', segments);
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[A-Za-z0-9._@+-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex RootSegmentPattern();
}
