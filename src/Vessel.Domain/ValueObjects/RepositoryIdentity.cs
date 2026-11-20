namespace Vessel.Domain.ValueObjects;

/// <summary>
///     Normalized repository identity (<c>host</c> + <c>owner/repo</c> path) used to match webhook deliveries to
///     applications. Matching is exact on the normalized path (case-insensitive, without <c>.git</c>) and on the host when
///     both sides know it, so <c>org/app</c> never matches <c>org/app-private</c>.
/// </summary>
public readonly record struct RepositoryIdentity
{
    private RepositoryIdentity(string? host, string path)
    {
        Host = host;
        Path = path;
    }

    /// <summary>Lower-case host without port or user info; null when the source did not include one.</summary>
    public string? Host { get; }

    /// <summary>Lower-case <c>owner/repo</c> (or <c>group/subgroup/repo</c>) path without <c>.git</c>.</summary>
    public string Path { get; }

    public static RepositoryIdentity? FromRepositoryUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return null;
        var value = repositoryUrl.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host) &&
            uri.Scheme is "http" or "https" or "ssh" or "git")
            return Create(uri.Host, uri.AbsolutePath);

        // scp-like syntax: user@host:owner/repo.git
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (at > 0 && colon > at + 1)
            return Create(value[(at + 1)..colon], value[(colon + 1)..]);

        return null;
    }

    /// <summary>Builds an identity from a provider full name (<c>owner/repo</c>) and optional web or clone URL.</summary>
    public static RepositoryIdentity? FromFullName(string? fullName, string? webUrl = null)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        RepositoryIdentity? fromUrl = FromRepositoryUrl(webUrl);
        return Create(fromUrl?.Host, fullName);
    }

    public bool Matches(RepositoryIdentity other)
    {
        if (!string.Equals(Path, other.Path, StringComparison.Ordinal)) return false;
        return Host is null || other.Host is null || string.Equals(Host, other.Host, StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return Host is null ? Path : $"{Host}/{Path}";
    }

    private static RepositoryIdentity? Create(string? host, string path)
    {
        var normalizedPath = Uri.UnescapeDataString(path).Trim().Trim('/');
        if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalizedPath = normalizedPath[..^4];
        normalizedPath = normalizedPath.TrimEnd('/').ToLowerInvariant();
        if (normalizedPath.Length == 0 || !normalizedPath.Contains('/', StringComparison.Ordinal) ||
            normalizedPath.Contains("//", StringComparison.Ordinal))
            return null;

        var normalizedHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim().ToLowerInvariant();
        return new RepositoryIdentity(normalizedHost, normalizedPath);
    }
}
