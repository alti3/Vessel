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

    /// <summary>Generates a Railpack build plan (<c>railpack prepare</c> in the pinned CLI image).</summary>
    IAsyncEnumerable<ProcessOutputLine> PrepareRailpackPlanAsync(
        ContainerRuntimeTarget target,
        RailpackPrepareCommand command,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot run Railpack builds.");
    }

    /// <summary>Builds a Railpack plan with BuildKit (buildx, docker-container builder) and loads the image.</summary>
    IAsyncEnumerable<ProcessOutputLine> BuildRailpackImageAsync(
        ContainerRuntimeTarget target,
        RailpackBuildCommand command,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot run Railpack builds.");
    }

    /// <summary>Generates the Nixpacks Dockerfile (<c>nixpacks build --out</c>) inside the build context.</summary>
    IAsyncEnumerable<ProcessOutputLine> GenerateNixpacksBuildAsync(
        ContainerRuntimeTarget target,
        NixpacksGenerateCommand command,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot run Nixpacks builds.");
    }

    /// <summary>Tags <paramref name="source" /> as <paramref name="destination" />. False on failure.</summary>
    Task<bool> TagImageAsync(
        ContainerRuntimeTarget target,
        string source,
        string destination,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot tag images.");
    }

    /// <summary>Pushes an image to its registry (logging in first when credentials are given).</summary>
    IAsyncEnumerable<ProcessOutputLine> PushImageAsync(
        ContainerRuntimeTarget target,
        RegistryImageCommand command,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot push images.");
    }

    /// <summary>Pulls an image from its registry (logging in first when credentials are given).</summary>
    IAsyncEnumerable<ProcessOutputLine> PullImageAsync(
        ContainerRuntimeTarget target,
        RegistryImageCommand command,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This container runtime client cannot pull images.");
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
