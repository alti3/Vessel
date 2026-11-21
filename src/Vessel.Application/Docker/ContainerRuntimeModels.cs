using Vessel.Application.Servers;
using Vessel.Domain.Servers;

using Vessel.Application.Processes;

namespace Vessel.Application.Docker;

public enum ContainerRuntimeProvider
{
    Docker,
    Podman
}

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
