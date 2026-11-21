using System.Globalization;
using System.Text.RegularExpressions;
using Vessel.Domain.Common;

namespace Vessel.Domain.Storage;

/// <summary>Which kind of resource owns a persistent volume or file mount.</summary>
public enum StorageOwnerKind
{
    Application = 0,
    Service = 1
}

/// <summary>
///     Validation rules for persistent storage. There are no arbitrary host paths (upstream Coolify v4.3.22 removed
///     <c>host_path</c>): volumes are named runtime volumes, and file or directory mounts live in the resource's owned
///     directory, addressed by a relative path made of conservative segments.
/// </summary>
public static partial class StoragePaths
{
    public const int MaxMountPathLength = 512;

    public const int MaxRelativePathLength = 400;

    public const int MaxRelativeSegments = 8;

    /// <summary>
    ///     Validates a container mount path: absolute POSIX path of conservative segments, not <c>/</c>, without traversal,
    ///     colons (the short compose syntax separator) or whitespace.
    /// </summary>
    public static string ValidateMountPath(string mountPath)
    {
        var value = DomainValidation.Required(mountPath, "Mount path", MaxMountPathLength);
        if (value.Length > 1) value = value.TrimEnd('/');
        if (!value.StartsWith('/') || value == "/")
            throw new DomainException("Mount path must be an absolute container path such as /data.");
        foreach (var segment in value[1..].Split('/'))
            if (segment.Length == 0 || segment is "." or ".." || !MountSegmentPattern().IsMatch(segment))
                throw new DomainException(
                    $"Mount path segment '{segment}' is invalid: use letters, digits, '.', '_', '-', '@' or '+'.");
        return value;
    }

    /// <summary>
    ///     Validates a path relative to the resource directory (<c>./</c> prefix accepted). Absolute paths, home paths,
    ///     variables and traversal are rejected: storage can never point at an arbitrary host path.
    /// </summary>
    public static string ValidateRelativePath(string path)
    {
        var value = DomainValidation.Required(path, "Path", MaxRelativePathLength);
        if (value.StartsWith('/') || value.StartsWith('\\') || value.StartsWith('~') || value.Contains('$') ||
            (value.Length > 1 && value[1] == ':'))
            throw new DomainException(
                "Host paths are not supported: the path must be relative to the resource directory (for example ./config/app.conf).");
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        value = value.TrimEnd('/');
        if (value.Contains('\\'))
            throw new DomainException("Path must use '/' separators.");
        var segments = value.Split('/');
        if (segments.Length == 0 || segments.Length > MaxRelativeSegments)
            throw new DomainException($"Path must have between 1 and {MaxRelativeSegments} segments.");
        foreach (var segment in segments)
            if (segment is "" or "." or ".." || !RelativeSegmentPattern().IsMatch(segment))
                throw new DomainException(
                    $"Path segment '{segment}' is invalid: use letters, digits, '.', '_' or '-' and start with a letter, digit or '_'.");
        return string.Join('/', segments);
    }

    /// <summary>Validates a user-facing volume name (lower case, used inside the runtime volume name).</summary>
    public static string ValidateVolumeName(string name)
    {
        var value = DomainValidation.Required(name, "Volume name", 63);
        return VolumeNamePattern().IsMatch(value)
            ? value
            : throw new DomainException(
                "Volume name must start with a lower-case letter or digit and contain only lower-case letters, digits, '_', '.' or '-'.");
    }

    /// <summary>Validates an optional compose service name (the service a mount is attached to).</summary>
    public static string? ValidateServiceName(string? serviceName)
    {
        var value = DomainValidation.Optional(serviceName, "Service name", 63);
        if (value is null) return null;
        return ServiceNamePattern().IsMatch(value)
            ? value
            : throw new DomainException("Service name is invalid.");
    }

    /// <summary>
    ///     Runtime (Docker/Podman) name of a persistent volume: <c>vessel-{ownerId:N}-{name}</c>, unique per server, plus a
    ///     <c>-pr-{n}</c> suffix for previews so a preview never shares a volume with production.
    /// </summary>
    public static string RuntimeVolumeName(Guid ownerId, string name, string? previewSuffix = null)
    {
        var baseName = $"vessel-{ownerId.ToString("N", CultureInfo.InvariantCulture)}-{ValidateVolumeName(name)}";
        return previewSuffix is null ? baseName : $"{baseName}-{previewSuffix}";
    }

    /// <summary>
    ///     Owned resource directory, relative to the server's owned root: <c>resources/{applications|services}/{id:N}</c>
    ///     (production) or <c>…/{id:N}-pr-{n}</c> (preview), so previews never share directories with production.
    /// </summary>
    public static string ResourceDirectory(StorageOwnerKind ownerKind, Guid ownerId, string? previewSuffix = null)
    {
        var kind = ownerKind == StorageOwnerKind.Application ? "applications" : "services";
        var id = ownerId.ToString("N", CultureInfo.InvariantCulture);
        return previewSuffix is null ? $"resources/{kind}/{id}" : $"resources/{kind}/{id}-{previewSuffix}";
    }

    [GeneratedRegex("^[A-Za-z0-9._@+-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex MountSegmentPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeSegmentPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeNamePattern();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();
}
