using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Proxy;

namespace Vessel.Application.Proxy;

public enum ProxyContainerState
{
    Unknown,
    Missing,
    Created,
    Running,
    Restarting,
    Paused,
    Exited,
    Dead
}

/// <summary>
///     Observed proxy container state from the server's real runtime. <c>Health</c> is the runtime healthcheck status
///     (<c>healthy</c>, <c>starting</c>, <c>unhealthy</c>) when the container defines one.
/// </summary>
public sealed record ProxyContainerStatus(
    ProxyContainerState State,
    string? Health,
    string? Image,
    IReadOnlyList<string> Networks)
{
    public static ProxyContainerStatus Missing { get; } = new(ProxyContainerState.Missing, null, null, []);

    /// <summary>Running, and the healthcheck (when present) reports healthy. A missing container is never healthy.</summary>
    public bool IsHealthy => State == ProxyContainerState.Running &&
                             (Health is null || string.Equals(Health, "healthy", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Controls the proxy container on a server's runtime (Docker/Podman, local or over SSH).</summary>
public interface IProxyContainerRuntime
{
    Task<ProxyContainerStatus> InspectAsync(ServerExecutionTarget target, CancellationToken cancellationToken = default);

    Task EnsureNetworkAsync(ServerExecutionTarget target, string network, CancellationToken cancellationToken = default);

    /// <summary>Starts (or recreates) the proxy from the compose file at <paramref name="composeHostPath" />.</summary>
    Task UpAsync(
        ServerExecutionTarget target,
        string composeHostPath,
        bool forceRecreate,
        CancellationToken cancellationToken = default);

    /// <summary>Stops and removes the proxy container. A missing container is not an error.</summary>
    Task StopAsync(ServerExecutionTarget target, CancellationToken cancellationToken = default);

    /// <summary>Connects the proxy container to <paramref name="network" />; already-connected is not an error.</summary>
    Task ConnectNetworkAsync(ServerExecutionTarget target, string network, CancellationToken cancellationToken = default);
}

/// <summary>Absolute host paths that the proxy compose file bind-mounts.</summary>
public sealed record ProxyHostPaths(string StaticConfigurationFile, string DynamicDirectory, string AcmeDirectory);

public sealed record ProxyRuntimeRenderRequest(
    ServerId ServerId,
    string Image,
    int HttpPort,
    int HttpsPort,
    string Network,
    ProxyHostPaths HostPaths);

public sealed record ProxyRuntimeDirectory(string RelativePath, UnixFileMode Mode);

public sealed record ProxyRuntimeFile(string RelativePath, string Contents, UnixFileMode Mode, bool CreateOnly = false);

/// <summary>Rendered proxy files. <c>Hash</c> covers the static configuration and compose file (last-applied static hash).</summary>
public sealed record ProxyRuntimeBundle(
    IReadOnlyList<ProxyRuntimeDirectory> Directories,
    IReadOnlyList<ProxyRuntimeFile> Files,
    string ComposeRelativePath,
    string Hash);

/// <summary>Renders the static proxy configuration and compose file for a server.</summary>
public interface IProxyRuntimeRenderer
{
    ServerProxyType ProxyType { get; }

    ProxyRuntimeBundle Render(ProxyRuntimeRenderRequest request);
}

/// <summary>Real proxy health for server health polling.</summary>
public interface IProxyStatusProbe
{
    /// <summary>
    ///     True when the server needs no proxy (type None) or the proxy container is running and healthy; false when the
    ///     container is missing, stopped, unhealthy, or its state cannot be read.
    /// </summary>
    Task<bool> IsHealthyAsync(Domain.Servers.Server server, CancellationToken cancellationToken = default);
}

public sealed record ServerProxySummary(
    Guid ServerId,
    string ProxyType,
    string Image,
    string RecommendedImage,
    bool ImageOutdated,
    int HttpPort,
    int HttpsPort,
    string DesiredState,
    string ContainerState,
    string? ContainerHealth,
    bool Healthy,
    bool RestartRequired,
    string? LastAppliedStaticHash,
    string? LastAppliedDynamicHash,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastStoppedAt,
    DateTimeOffset? LastDynamicAppliedAt,
    string? LastError,
    string? StatusError);

/// <summary>PATCH body. <c>ProxyType</c> is <c>Traefik</c> or <c>None</c> (case-insensitive); <c>Caddy</c> is reserved.</summary>
public sealed record UpdateServerProxySettingsRequest(string? ProxyType, int? HttpPort, int? HttpsPort);

public sealed record ProxyEnsureResult(bool Succeeded, bool Skipped, string Message);
