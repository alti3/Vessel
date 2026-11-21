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
public sealed record DockerfileStackSpec(
    string ServiceName,
    string ImageName,
    string BuildContext,
    string DockerfilePath,
    string NetworkName,
    string UpstreamAlias,
    string HostPortBinding,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
///     Repository compose input. <see cref="RepositoryDirectory" /> is the absolute checkout path and the path containment
///     root; <see cref="ComposeFilePath" /> is relative to it; <see cref="PreviewSuffix" /> is null for production and
///     <c>pr-{n}</c> for previews.
/// </summary>
public sealed record RepositoryComposeSpec(
    string RepositoryDirectory,
    string ComposeFilePath,
    string NetworkName,
    string UpstreamAlias,
    int ExposedPort,
    IReadOnlyDictionary<string, string> Labels,
    string? PreviewSuffix);

/// <summary>
///     Normalized compose document, the primary service (attached to the server network and targeted by the proxy) and
///     non-fatal notes for the deployment log (never secret values).
/// </summary>
public sealed record ComposeNormalizationResult(
    string Yaml,
    string PrimaryService,
    IReadOnlyList<string> Warnings);

public sealed class ComposeValidationException(string message) : Exception(message);
