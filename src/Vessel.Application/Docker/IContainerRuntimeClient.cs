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
}
