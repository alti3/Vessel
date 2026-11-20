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
}
