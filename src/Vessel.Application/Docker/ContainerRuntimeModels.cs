using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain.Servers;

namespace Vessel.Application.Docker;

public enum ContainerRuntimeProvider
{
    Docker,
    Podman
}

/// <summary>Where and how container runtime commands run for one server.</summary>
/// <param name="Provider">The runtime family (Docker or Podman).</param>
/// <param name="ApiEndpoint">Optional runtime API endpoint for local runtimes.</param>
/// <param name="CliExecutable">Optional runtime CLI override; defaults to the provider's CLI name.</param>
/// <param name="Remote">
///     Set for SSH servers: the runtime CLI runs on the remote host over SSH (through <c>IServerCommandRunner</c>), never
///     against the control-plane host's runtime.
/// </param>
public sealed record ContainerRuntimeTarget(
    ContainerRuntimeProvider Provider,
    Uri? ApiEndpoint = null,
    string? CliExecutable = null,
    ServerExecutionTarget? Remote = null)
{
    public bool IsRemote => Remote is not null;

    /// <summary>The runtime of a server, routed over its own connection (local runtime or SSH).</summary>
    public static ContainerRuntimeTarget For(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var provider = server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker;
        return new ContainerRuntimeTarget(provider,
            Remote: server.ConnectionType == ServerConnectionType.Ssh ? ServerExecutionTarget.From(server) : null);
    }

    /// <summary>
    /// The runtime CLI used for this target: an explicit <see cref="CliExecutable" />, otherwise the provider's
    /// default binary (<c>docker</c> or <c>podman</c>).
    /// </summary>
    public string ResolveCliExecutable()
    {
        if (!string.IsNullOrWhiteSpace(CliExecutable)) return CliExecutable;
        return Provider == ContainerRuntimeProvider.Podman ? "podman" : "docker";
    }
}

public sealed record ContainerRuntimeInfo(
    ContainerRuntimeProvider Provider,
    string Version,
    string ApiVersion,
    string OperatingSystem,
    string Architecture);

public sealed record ContainerSummary(
    string Id,
    IReadOnlyList<string> Names,
    string Image,
    string State,
    string Status,
    IReadOnlyDictionary<string, string> Labels);

public sealed record ImageSummary(string Id, IReadOnlyList<string> RepoTags, long Size);

public sealed record NetworkSummary(string Id, string Name, string Driver);

public sealed record VolumeSummary(string Name, string Driver, string Mountpoint);

public sealed record ContainerEvent(string Type, string Action, string ActorId, DateTimeOffset Timestamp);

/// <summary>A compose invocation. <paramref name="Redaction" /> carries the deployment's secret context for its output.</summary>
public sealed record ComposeCommand(
    string WorkingDirectory,
    IReadOnlyList<string> ComposeFiles,
    IReadOnlyList<string> Arguments,
    string? EnvironmentFile = null,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);

/// <summary>An image build. Build argument values are always redacted, plus anything in <paramref name="Redaction" />.</summary>
public sealed record DockerBuildCommand(
    string WorkingDirectory,
    string DockerfilePath,
    string ImageName,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> BuildArguments,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);

/// <summary>
///     One container from <c>inspect</c> (never its environment or command line): identity, image, labels and state,
///     including the health and restart count used by status reconciliation (SRV-04) and cleanup (SRV-07).
/// </summary>
public sealed record ContainerStateReading(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string? Health,
    int RestartCount,
    int? ExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>One local image with size, creation time and labels (server Images page, cleanup planning).</summary>
public sealed record ImageDetails(
    string Id,
    IReadOnlyList<string> RepoTags,
    long SizeBytes,
    DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>Options of a container log read (<c>docker logs</c>). Lines always carry the runtime timestamp.</summary>
/// <param name="Tail">Last lines per container; null reads the whole log.</param>
/// <param name="Since">Only lines at or after this time.</param>
/// <param name="Follow">Keep streaming new lines until cancelled.</param>
public sealed record ContainerLogOptions(int? Tail, DateTimeOffset? Since, bool Follow);

public enum RuntimePruneKind
{
    /// <summary>Dangling (untagged) images only.</summary>
    DanglingImages = 0,

    /// <summary>Builder cache (<c>docker builder prune -af</c>).</summary>
    BuildCache = 1,

    /// <summary>Unused networks.</summary>
    Networks = 2,

    /// <summary>Unused anonymous volumes only (never named volumes).</summary>
    AnonymousVolumes = 3
}

public sealed record RuntimePruneResult(bool Succeeded, long ReclaimedBytes, bool Supported = true);
