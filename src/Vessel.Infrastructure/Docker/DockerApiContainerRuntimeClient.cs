using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Vessel.Application.Docker;
using Vessel.Application.Processes;

namespace Vessel.Infrastructure.Docker;

public sealed class DockerApiContainerRuntimeClient(DockerCliContainerRuntimeClient cliFallback)
    : IContainerRuntimeClient
{
    public async Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        if (target.IsRemote) return await cliFallback.GetInfoAsync(target, cancellationToken);
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
        if (target.IsRemote) return await cliFallback.ListContainersAsync(target, all, cancellationToken);
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
        if (target.IsRemote) return await cliFallback.InspectContainerAsync(target, containerId, cancellationToken);
        using DockerClient client = CreateClient(target);
        ContainerInspectResponse container =
            await client.Containers.InspectContainerAsync(containerId, cancellationToken);
        return JsonSerializer.Serialize(container);
    }

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(
        ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        if (target.IsRemote) return await cliFallback.ListImagesAsync(target, cancellationToken);
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
        if (target.IsRemote) return await cliFallback.ListNetworksAsync(target, cancellationToken);
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
        if (target.IsRemote) return await cliFallback.ListVolumesAsync(target, cancellationToken);
        using DockerClient client = CreateClient(target);
        VolumesListResponse volumes = await client.Volumes.ListAsync(cancellationToken);
        return volumes.Volumes.Select(volume => new VolumeSummary(
                volume.Name,
                volume.Driver,
                volume.Mountpoint))
            .ToArray();
    }

    public Task EnsureNetworkAsync(
        ContainerRuntimeTarget target,
        string name,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        return cliFallback.EnsureNetworkAsync(target, name, labels, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(
        ContainerRuntimeTarget target,
        DockerBuildCommand command,
        CancellationToken cancellationToken = default)
    {
        return cliFallback.BuildImageAsync(target, command, cancellationToken);
    }

    public async IAsyncEnumerable<ContainerEvent> StreamEventsAsync(
        ContainerRuntimeTarget target,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (target.IsRemote)
        {
            await foreach (ContainerEvent remoteEvent in cliFallback.StreamEventsAsync(target, cancellationToken))
                yield return remoteEvent;
            yield break;
        }

        using DockerClient client = CreateClient(target);
        var progress = new DockerEventProgress();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitor = client.System.MonitorEventsAsync(new ContainerEventsParameters(), progress, linked.Token);

        await foreach (ContainerEvent item in progress.ReadAllAsync(cancellationToken)) yield return item;

        await monitor;
    }

    public IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(
        ContainerRuntimeTarget target,
        ComposeCommand command,
        CancellationToken cancellationToken = default)
    {
        return cliFallback.RunComposeAsync(target, command, cancellationToken);
    }

    public Task<bool> RemoveComposeProjectAsync(
        ContainerRuntimeTarget target,
        string projectName,
        bool removeVolumes,
        CancellationToken cancellationToken = default)
    {
        return cliFallback.RemoveComposeProjectAsync(target, projectName, removeVolumes, cancellationToken);
    }

    public Task<bool> RemoveImageAsync(
        ContainerRuntimeTarget target,
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        return cliFallback.RemoveImageAsync(target, imageReference, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> PrepareRailpackPlanAsync(ContainerRuntimeTarget target,
        RailpackPrepareCommand command, CancellationToken cancellationToken = default)
    {
        return cliFallback.PrepareRailpackPlanAsync(target, command, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> BuildRailpackImageAsync(ContainerRuntimeTarget target,
        RailpackBuildCommand command, CancellationToken cancellationToken = default)
    {
        return cliFallback.BuildRailpackImageAsync(target, command, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> GenerateNixpacksBuildAsync(ContainerRuntimeTarget target,
        NixpacksGenerateCommand command, CancellationToken cancellationToken = default)
    {
        return cliFallback.GenerateNixpacksBuildAsync(target, command, cancellationToken);
    }

    public Task<bool> TagImageAsync(ContainerRuntimeTarget target, string source, string destination,
        CancellationToken cancellationToken = default)
    {
        return cliFallback.TagImageAsync(target, source, destination, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> PushImageAsync(ContainerRuntimeTarget target,
        RegistryImageCommand command, CancellationToken cancellationToken = default)
    {
        return cliFallback.PushImageAsync(target, command, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> PullImageAsync(ContainerRuntimeTarget target,
        RegistryImageCommand command, CancellationToken cancellationToken = default)
    {
        return cliFallback.PullImageAsync(target, command, cancellationToken);
    }

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
        private readonly Channel<ContainerEvent> _channel =
            Channel.CreateUnbounded<ContainerEvent>();

        public void Report(Message value)
        {
            _channel.Writer.TryWrite(new ContainerEvent(
                value.Type ?? string.Empty,
                value.Action ?? string.Empty,
                value.Actor?.ID ?? string.Empty,
                DateTimeOffset.FromUnixTimeSeconds(value.Time)));
        }

        public IAsyncEnumerable<ContainerEvent> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
