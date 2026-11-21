using Vessel.Domain.Common;
using Vessel.Domain.ValueObjects;

namespace Vessel.Domain.Applications;

/// <summary>
///     How an application is built (DEP-09).
/// </summary>
/// <param name="BuildPack">The build pack.</param>
/// <param name="BaseDirectory">Repository directory the build runs in (<c>/</c> is the repository root).</param>
/// <param name="DockerfilePath">
///     Dockerfile path for the Dockerfile pack, or the compose file path for the Docker Compose pack, relative to the
///     base directory.
/// </param>
/// <param name="InstallCommand">Railpack/Nixpacks install command override.</param>
/// <param name="BuildCommand">Railpack/Nixpacks build command override.</param>
/// <param name="StartCommand">Railpack/Nixpacks start command override.</param>
/// <param name="ImageTag">Reserved image tag override.</param>
/// <param name="PublishDirectory">Static pack: directory (relative to the base directory) that nginx serves.</param>
/// <param name="IsSpa">Static pack: unknown paths fall back to <c>/index.html</c> (single-page applications).</param>
/// <param name="RegistryImage">
///     Optional registry repository (for example <c>ghcr.io/acme/web</c>) that production images are pushed to after the
///     build. Pushed images are rollback targets that can be pulled again when the local image is gone (DEP-06).
/// </param>
/// <param name="RegistryCredentialId">Team registry credential used to push and pull <see cref="RegistryImage" />.</param>
/// <param name="ComposeService">
///     Docker Compose pack: the service that receives the application's public domains. Null selects the service that
///     exposes the application port (or the first service that builds or publishes ports).
/// </param>
public readonly record struct BuildConfiguration(
    ApplicationBuildPack BuildPack,
    string BaseDirectory,
    string? DockerfilePath,
    string? InstallCommand,
    string? BuildCommand,
    string? StartCommand,
    ImageTag? ImageTag,
    string? PublishDirectory = null,
    bool IsSpa = false,
    string? RegistryImage = null,
    RegistryCredentialId? RegistryCredentialId = null,
    string? ComposeService = null)
{
    public const int MaxCommandLength = 1000;
    public const int MaxPathLength = 512;

    public static BuildConfiguration Default(ApplicationBuildPack buildPack)
    {
        return new BuildConfiguration(buildPack, "/", null, null, null, null, null);
    }

    /// <summary>
    ///     Validates and normalizes the configuration: known build pack, relative paths without traversal or control
    ///     characters, bounded commands without NUL or line breaks, a registry repository without tag, and a compose
    ///     service name of the Compose grammar.
    /// </summary>
    public BuildConfiguration Validated()
    {
        if (!Enum.IsDefined(BuildPack)) throw new DomainException("Build pack is not supported.");
        return this with
        {
            BaseDirectory = NormalizeBaseDirectory(BaseDirectory),
            DockerfilePath = OptionalRelativePath(DockerfilePath, "Dockerfile or compose file path"),
            InstallCommand = OptionalCommand(InstallCommand, "Install command"),
            BuildCommand = OptionalCommand(BuildCommand, "Build command"),
            StartCommand = OptionalCommand(StartCommand, "Start command"),
            PublishDirectory = OptionalRelativePath(PublishDirectory, "Publish directory"),
            RegistryImage = string.IsNullOrWhiteSpace(RegistryImage)
                ? null
                : ImageReference.ParseRepository(RegistryImage, "registry image").Repository,
            RegistryCredentialId = string.IsNullOrWhiteSpace(RegistryImage) ? null : RegistryCredentialId,
            ComposeService = string.IsNullOrWhiteSpace(ComposeService)
                ? null
                : ContainerName.Parse(ComposeService, "compose service").Value
        };
    }

    private static string NormalizeBaseDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() is "/" or ".") return "/";
        return "/" + RelativePath(value, "Base directory");
    }

    private static string? OptionalRelativePath(string? value, string field)
    {
        return string.IsNullOrWhiteSpace(value) ? null : RelativePath(value, field);
    }

    private static string RelativePath(string value, string field)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > MaxPathLength) throw new DomainException($"{field} is too long.");
        if (trimmed.Any(char.IsControl)) throw new DomainException($"{field} must not contain control characters.");
        var normalized = trimmed.Replace('\\', '/');
        if (normalized.Length > 1 && normalized[1] == ':')
            throw new DomainException($"{field} must be relative to the repository.");
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        if (segments.Any(segment => segment == ".."))
            throw new DomainException($"{field} must not contain '..'.");
        if (segments.Length == 0) throw new DomainException($"{field} is empty.");
        return string.Join('/', segments);
    }

    private static string? OptionalCommand(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > MaxCommandLength)
            throw new DomainException($"{field} must be {MaxCommandLength} characters or fewer.");
        if (trimmed.Contains('\0', StringComparison.Ordinal) || trimmed.Contains('\n', StringComparison.Ordinal) ||
            trimmed.Contains('\r', StringComparison.Ordinal))
            throw new DomainException($"{field} must be a single line.");
        return trimmed;
    }
}

/// <summary>
///     Build pack catalog: every <see cref="ApplicationBuildPack" /> is implemented. Nixpacks is deprecated like upstream
///     (v4.5.0): it still deploys (it needs the <c>nixpacks</c> CLI on the server) and Railpack is recommended instead.
/// </summary>
public static class ApplicationBuildPacks
{
    public static ApplicationBuildPack Recommended => ApplicationBuildPack.Railpack;

    public static bool IsDeprecated(ApplicationBuildPack buildPack)
    {
        return buildPack == ApplicationBuildPack.Nixpacks;
    }

    public static string DisplayName(ApplicationBuildPack buildPack)
    {
        return buildPack switch
        {
            ApplicationBuildPack.Nixpacks => "Nixpacks (deprecated)",
            ApplicationBuildPack.Railpack => "Railpack (recommended)",
            ApplicationBuildPack.Static => "Static (nginx)",
            ApplicationBuildPack.Dockerfile => "Dockerfile",
            ApplicationBuildPack.DockerCompose => "Docker Compose",
            _ => buildPack.ToString()
        };
    }

    public const string NixpacksDeprecationMessage =
        "Nixpacks is deprecated (as upstream Coolify v4.5.0). Switch the application to the Railpack build pack.";
}
