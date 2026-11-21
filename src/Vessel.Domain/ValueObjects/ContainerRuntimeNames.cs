using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.ValueObjects;

/// <summary>
///     A container name that is safe to pass as a positional container runtime argument: Docker's own name grammar
///     (<c>[a-zA-Z0-9][a-zA-Z0-9_.-]</c>), at most 128 characters. It can never start with <c>-</c> (option injection)
///     or contain whitespace, quotes or path separators (SEC-10, upstream v4.4.0 #11912).
/// </summary>
public readonly partial record struct ContainerName
{
    public const int MaxLength = 128;

    private ContainerName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ContainerName Parse(string? value, string context = "container name")
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!IsValid(trimmed))
            throw new DomainException(
                $"Invalid {context}: use 1 to {MaxLength} letters, digits, '_', '.' or '-', starting with a letter or digit.");
        return new ContainerName(trimmed);
    }

    public static bool IsValid(string? value)
    {
        return value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value);
    }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
///     A container network name (same grammar as <see cref="ContainerName" />, at most 128 characters), validated before
///     it reaches <c>network create</c>/<c>network inspect</c> or a generated compose file.
/// </summary>
public readonly partial record struct ContainerNetworkName
{
    public const int MaxLength = 128;

    private ContainerNetworkName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ContainerNetworkName Parse(string? value, string context = "network name")
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!IsValid(trimmed))
            throw new DomainException(
                $"Invalid {context}: use 1 to {MaxLength} letters, digits, '_', '.' or '-', starting with a letter or digit.");
        return new ContainerNetworkName(trimmed);
    }

    public static bool IsValid(string? value)
    {
        return value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value);
    }

    public override string ToString()
    {
        return Value;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
///     A compose project name (<c>docker compose --project-name</c>): lower-case letters, digits, '_' and '-', starting
///     with a letter or digit, as the Compose specification requires.
/// </summary>
public static partial class ComposeProjectName
{
    public const int MaxLength = 128;

    public static string Validate(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxLength || !Pattern().IsMatch(trimmed))
            throw new DomainException(
                "Invalid compose project name: use lower-case letters, digits, '_' or '-', starting with a letter or digit.");
        return trimmed;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
///     An OCI/Docker image reference (<c>[registry[:port]/]path[:tag][@digest]</c>) following the
///     <c>distribution/reference</c> grammar: lower-case path components, a tag of at most 128 characters and an
///     optional <c>algorithm:hex</c> digest. It never starts with <c>-</c>, so it can be passed positionally to
///     <c>build --tag</c>, <c>push</c>, <c>pull</c>, <c>tag</c> or <c>image rm</c> (SEC-10).
/// </summary>
public readonly partial record struct ImageReference
{
    public const int MaxNameLength = 255;

    private ImageReference(string repository, string? tag, string? digest)
    {
        Repository = repository;
        Tag = tag;
        Digest = digest;
    }

    /// <summary>Registry host (if any) plus path, without tag or digest.</summary>
    public string Repository { get; }

    public string? Tag { get; }

    public string? Digest { get; }

    public string Value => Repository + (Tag is null ? string.Empty : ":" + Tag) +
                           (Digest is null ? string.Empty : "@" + Digest);

    /// <summary>
    ///     Registry host when the first path component looks like one (contains '.' or ':' or is <c>localhost</c>);
    ///     otherwise null (Docker Hub).
    /// </summary>
    public string? Registry
    {
        get
        {
            var slash = Repository.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0) return null;
            var first = Repository[..slash];
            return first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) ||
                   first == "localhost"
                ? first
                : null;
        }
    }

    public static ImageReference Parse(string? value, string context = "image reference")
    {
        return TryParse(value, out ImageReference reference)
            ? reference
            : throw new DomainException(
                $"Invalid {context}: expected [registry/]name[:tag][@digest] with lower-case name components.");
    }

    public static bool TryParse(string? value, out ImageReference reference)
    {
        reference = default;
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > 512) return false;
        Match match = ReferencePattern().Match(trimmed);
        if (!match.Success) return false;
        var name = match.Groups["name"].Value;
        if (name.Length > MaxNameLength) return false;
        reference = new ImageReference(name,
            match.Groups["tag"].Success ? match.Groups["tag"].Value : null,
            match.Groups["digest"].Success ? match.Groups["digest"].Value : null);
        return true;
    }

    /// <summary>Parses a repository (no tag, no digest), for example a registry push target.</summary>
    public static ImageReference ParseRepository(string? value, string context = "image repository")
    {
        ImageReference reference = Parse(value, context);
        if (reference.Tag is not null || reference.Digest is not null)
            throw new DomainException($"Invalid {context}: give the repository without a tag or digest.");
        return reference;
    }

    /// <summary>The same repository with <paramref name="tag" /> (validated as an image tag).</summary>
    public ImageReference WithTag(string tag)
    {
        if (!TagPattern().IsMatch(tag)) throw new DomainException("Container image tag is invalid.");
        return new ImageReference(Repository, tag, null);
    }

    public override string ToString()
    {
        return Value;
    }

    // distribution/reference: domain (optional, with port) + path components, then :tag and @digest.
    [GeneratedRegex(
        @"^(?<name>(?:(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9])(?:\.(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]))*(?::[0-9]+)?/)?[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*)(?::(?<tag>[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}))?(?:@(?<digest>[A-Za-z][A-Za-z0-9]*(?:[-_+.][A-Za-z][A-Za-z0-9]*)*:[0-9a-fA-F]{32,}))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
