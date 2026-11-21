using Vessel.Application.Processes;

namespace Vessel.Application.Docker;

public interface IContainerRuntimeClient
{
    Task<ContainerRuntimeInfo> GetInfoAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        ContainerRuntimeTarget target,
        bool all,
        CancellationToken cancellationToken = default);

    Task<string> InspectContainerAsync(
        ContainerRuntimeTarget target,
        string containerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImageSummary>> ListImagesAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default);

    Task EnsureNetworkAsync(
        ContainerRuntimeTarget target,
        string name,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(
        ContainerRuntimeTarget target,
        DockerBuildCommand command,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ContainerEvent> StreamEventsAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(
        ContainerRuntimeTarget target,
        ComposeCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops and removes the containers and project networks of one compose project (<c>compose down</c>), and its
    ///     named volumes when <paramref name="removeVolumes" /> is set. Needs no compose file. Returns false when the
    ///     runtime reported a failure.
    /// </summary>
    Task<bool> RemoveComposeProjectAsync(
        ContainerRuntimeTarget target,
        string projectName,
        bool removeVolumes,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot remove compose projects.");
    }

    /// <summary>Removes one image reference (never forced: an image used by a container stays). False on failure.</summary>
    Task<bool> RemoveImageAsync(
        ContainerRuntimeTarget target,
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot remove images.");
    }

    /// <summary>
    ///     Every container (running or not) with its labels, image id, health and restart count, from one structured
    ///     <c>inspect</c> that never reads environment variables or commands.
    /// </summary>
    Task<IReadOnlyList<ContainerStateReading>> ListContainerStatesAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot inspect containers.");
    }

    /// <summary>Every local image with its size, creation time and labels.</summary>
    Task<IReadOnlyList<ImageDetails>> ListImageDetailsAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot list image details.");
    }

    /// <summary>
    ///     Streams one container's log (stdout and stderr). Every line starts with the runtime's RFC 3339 timestamp
    ///     (<c>--timestamps</c>), so callers can merge replica streams.
    /// </summary>
    IAsyncEnumerable<ProcessOutputLine> StreamContainerLogsAsync(
        ContainerRuntimeTarget target,
        string containerId,
        ContainerLogOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot read container logs.");
    }

    /// <summary>Stops containers (they stay visible; never removed). False when the runtime reported a failure.</summary>
    Task<bool> StopContainersAsync(
        ContainerRuntimeTarget target,
        IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot stop containers.");
    }

    /// <summary>Removes stopped containers (never forced, never their volumes). Returns the number removed.</summary>
    Task<int> RemoveContainersAsync(
        ContainerRuntimeTarget target,
        IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot remove containers.");
    }

    /// <summary>Runs one prune step and reports the reclaimed space when the runtime prints it.</summary>
    Task<RuntimePruneResult> PruneAsync(
        ContainerRuntimeTarget target,
        RuntimePruneKind kind,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot prune.");
    }
}
