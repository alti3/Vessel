using Docker.DotNet;
using Docker.DotNet.Models;
using Vessel.Application.Docker;
using Vessel.Application.Processes;

namespace Vessel.Infrastructure.Docker;

public sealed class DockerApiContainerRuntimeClient(DockerCliContainerRuntimeClient cliFallback) : IContainerRuntimeClient
{
    public async Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        SystemInfoResponse info = await client.System.GetSystemInfoAsync(cancellationToken);
        VersionResponse version = await client.System.GetVersionAsync(cancellationToken);
        return new ContainerRuntimeInfo(
            target.Provider,
            version.Version ?? string.Empty,
            version.APIVersion ?? string.Empty,
            info.OperatingSystem ?? string.Empty,
            info.Architecture ?? string.Empty);
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        ContainerRuntimeTarget target,
        bool all,
        CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        IList<ContainerListResponse> containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters { All = all },
            cancellationToken);
        return containers.Select(container => new ContainerSummary(
                container.ID,
                container.Names.ToArray(),
                container.Image,
                container.State,
                container.Status,
                container.Labels.ToDictionary(StringComparer.Ordinal)))
            .ToArray();
    }

    public async Task<string> InspectContainerAsync(
        ContainerRuntimeTarget target,
        string containerId,
        CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        ContainerInspectResponse container = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(container);
    }

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        IList<ImagesListResponse> images = await client.Images.ListImagesAsync(
            new ImagesListParameters { All = true },
            cancellationToken);
        return images.Select(image => new ImageSummary(
                image.ID,
                image.RepoTags?.ToArray() ?? [],
                image.Size))
            .ToArray();
    }

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        IList<NetworkResponse> networks = await client.Networks.ListNetworksAsync(cancellationToken: cancellationToken);
        return networks.Select(network => new NetworkSummary(
                network.ID,
                network.Name,
                network.Driver))
            .ToArray();
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        VolumesListResponse volumes = await client.Volumes.ListAsync(cancellationToken);
        return volumes.Volumes.Select(volume => new VolumeSummary(
                volume.Name,
                volume.Driver,
                volume.Mountpoint))
            .ToArray();
    }

    public async IAsyncEnumerable<ContainerEvent> StreamEventsAsync(
        ContainerRuntimeTarget target,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using DockerClient client = CreateClient(target);
        var progress = new DockerEventProgress();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitor = client.System.MonitorEventsAsync(new ContainerEventsParameters(), progress, linked.Token);

        await foreach (ContainerEvent item in progress.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }

        await monitor;
    }

    public IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(
        ContainerRuntimeTarget target,
        ComposeCommand command,
        CancellationToken cancellationToken = default) =>
        cliFallback.RunComposeAsync(target, command, cancellationToken);

    private static DockerClient CreateClient(ContainerRuntimeTarget target)
    {
        Uri endpoint = target.ApiEndpoint ?? DefaultDockerEndpoint();
        return new DockerClientConfiguration(endpoint).CreateClient();
    }

    private static Uri DefaultDockerEndpoint()
    {
        if (OperatingSystem.IsWindows()) return new Uri("npipe://./pipe/docker_engine");
        return new Uri("unix:///var/run/docker.sock");
    }

    private sealed class DockerEventProgress : IProgress<Message>
    {
        private readonly System.Threading.Channels.Channel<ContainerEvent> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<ContainerEvent>();

        public void Report(Message value)
        {
            _channel.Writer.TryWrite(new ContainerEvent(
                value.Type ?? string.Empty,
                value.Action ?? string.Empty,
                value.Actor?.ID ?? string.Empty,
                DateTimeOffset.FromUnixTimeSeconds(value.Time)));
        }

        public IAsyncEnumerable<ContainerEvent> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
