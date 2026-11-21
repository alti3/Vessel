namespace Vessel.Application.Deployments;

/// <summary>
///     Generates and normalizes Docker Compose documents for deployments. Implemented in Infrastructure with a YAML
///     representation model, so generated YAML is always well formed and repository compose files are parsed rather than
///     scanned as text.
/// </summary>
public interface IComposeDocumentBuilder
{
    /// <summary>Generates the single-service stack used by Dockerfile applications.</summary>
    string CreateDockerfileStack(DockerfileStackSpec spec);

    /// <summary>
    ///     Parses a repository compose file and returns a normalized document: relative build contexts, bind mounts, env
    ///     files and extends files are resolved against the compose file directory and must stay inside the repository;
    ///     the primary service joins the server network with the upstream alias; previews get namespaced container,
    ///     volume and network names and no fixed host ports.
    /// </summary>
    /// <exception cref="ComposeValidationException">The document is invalid or violates the path policy.</exception>
    ComposeNormalizationResult NormalizeRepositoryCompose(string yaml, RepositoryComposeSpec spec);

    /// <summary>
    ///     Repository-relative paths of the files a compose document includes (<c>include:</c>), resolved against
    ///     <paramref name="composeFilePath" /> and required to stay inside the repository. The caller reads them (and their
    ///     own includes) and passes them in <see cref="RepositoryComposeSpec.IncludedFiles" />.
    /// </summary>
    /// <exception cref="ComposeValidationException">The document or an include path is invalid.</exception>
    IReadOnlyList<string> ListIncludes(string yaml, string repositoryDirectory, string composeFilePath);

    /// <summary>
    ///     Joins the named services of a (Vessel-generated) compose document to the external server network under the
    ///     given upstream aliases, so the server proxy can route to them. Used for service-template resources.
    /// </summary>
    /// <exception cref="ComposeValidationException">A named service does not exist or cannot join the network.</exception>
    string AttachServicesToNetwork(string yaml, string networkName, IReadOnlyDictionary<string, string> aliasesByService);
}

/// <summary>
///     Dockerfile stack input. <see cref="HostPortBinding" /> is <c>"{port}:{port}"</c> for production; previews publish
///     on an ephemeral loopback port (<c>"127.0.0.1::{port}"</c>) so they never collide with production or each other.
/// </summary>
/// <remarks>
///     <see cref="BuildContext" /> and <see cref="DockerfilePath" /> are null for images Vessel already built (every
///     build pack except Docker Compose): the stack then only references the image, so <c>compose up</c> never rebuilds
///     it without the build values (DEP-09).
/// </remarks>
public sealed record DockerfileStackSpec(
    string ServiceName,
    string ImageName,
    string? BuildContext,
    string? DockerfilePath,
    string NetworkName,
    string UpstreamAlias,
    string HostPortBinding,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
///     Repository compose input. <see cref="RepositoryDirectory" /> is the absolute checkout path and the path containment
///     root; <see cref="ComposeFilePath" /> is relative to it; <see cref="PreviewSuffix" /> is null for production and
///     <c>pr-{n}</c> for previews. <see cref="HostRepositoryDirectory" /> is set for SSH servers: paths are validated
///     against the local checkout and written relative to the repository's uploaded copy on the server.
/// </summary>
public sealed record RepositoryComposeSpec(
    string RepositoryDirectory,
    string ComposeFilePath,
    string NetworkName,
    string UpstreamAlias,
    int ExposedPort,
    IReadOnlyDictionary<string, string> Labels,
    string? PreviewSuffix,
    string? HostRepositoryDirectory = null)
{
    /// <summary>
    ///     Allows host-level settings (SEC-11): <c>privileged</c>, host network/pid/ipc/uts/userns/cgroup namespaces,
    ///     dangerous <c>cap_add</c>, devices, unconfined security options, absolute host bind mounts (including the
    ///     runtime socket) and host-path volume drivers. Only set when the deployment is authorized by an instance
    ///     administrator; every use is reported in <see cref="ComposeNormalizationResult.ElevatedSettings" />.
    /// </summary>
    public bool AllowElevated { get; init; }

    /// <summary>
    ///     Contents of files named by <c>include:</c> (repository-relative paths, see
    ///     <see cref="IComposeDocumentBuilder.ListIncludes" />). Included files must be inside the repository.
    /// </summary>
    public IReadOnlyDictionary<string, string> IncludedFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Configured public service (<c>BuildConfiguration.ComposeService</c>); must exist when set.</summary>
    public string? PrimaryService { get; init; }

    /// <summary>
    ///     Build-time variable names injected as <c>build.args</c> (<c>KEY: ${KEY}</c>, from the env file) into every
    ///     service that builds, unless the service already declares that argument.
    /// </summary>
    public IReadOnlyList<string> BuildArgumentKeys { get; init; } = [];
}

/// <summary>
///     Normalized compose document, the primary service (attached to the server network and targeted by the proxy) and
///     non-fatal notes for the deployment log (never secret values). <see cref="ElevatedSettings" /> lists host-level
///     settings that were allowed because the deployment is authorized by an instance administrator (logged and
///     audited).
/// </summary>
public sealed record ComposeNormalizationResult(
    string Yaml,
    string PrimaryService,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> ElevatedSettings { get; init; } = [];
}

public sealed class ComposeValidationException(string message) : Exception(message);
