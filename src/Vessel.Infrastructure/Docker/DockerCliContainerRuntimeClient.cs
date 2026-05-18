using System.Text.Json;
using Vessel.Application.Docker;
using Vessel.Application.Processes;

namespace Vessel.Infrastructure.Docker;

public sealed class DockerCliContainerRuntimeClient(IProcessRunner processRunner) : IContainerRuntimeClient
{
    public async Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["version", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement server = document.RootElement.GetProperty("Server");
        return new ContainerRuntimeInfo(
            target.Provider,
            server.GetProperty("Version").GetString() ?? string.Empty,
            server.GetProperty("APIVersion").GetString() ?? string.Empty,
            server.GetProperty("Os").GetString() ?? string.Empty,
            server.GetProperty("Arch").GetString() ?? string.Empty);
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "ps", "--format", "{{json .}}" };
        if (all) args.Insert(1, "-a");
        ProcessResult result = await RunDockerAsync(target, args, cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new ContainerSummary(
                    root.GetProperty("ID").GetString() ?? string.Empty,
                    [(root.GetProperty("Names").GetString() ?? string.Empty)],
                    root.GetProperty("Image").GetString() ?? string.Empty,
                    root.TryGetProperty("State", out JsonElement state) ? state.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("Status", out JsonElement status) ? status.GetString() ?? string.Empty : string.Empty,
                    new Dictionary<string, string>());
            })
            .ToArray();
    }

    public async Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["inspect", containerId], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput;
    }

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["images", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new ImageSummary(
                    root.GetProperty("ID").GetString() ?? string.Empty,
                    [$"{root.GetProperty("Repository").GetString()}:{root.GetProperty("Tag").GetString()}"],
                    0);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["network", "ls", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new NetworkSummary(
                    root.GetProperty("ID").GetString() ?? string.Empty,
                    root.GetProperty("Name").GetString() ?? string.Empty,
                    root.GetProperty("Driver").GetString() ?? string.Empty);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["volume", "ls", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new VolumeSummary(
                    root.GetProperty("Name").GetString() ?? string.Empty,
                    root.GetProperty("Driver").GetString() ?? string.Empty,
                    string.Empty);
            })
            .ToArray();
    }

    public async IAsyncEnumerable<ContainerEvent> StreamEventsAsync(
        ContainerRuntimeTarget target,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ProcessOutputLine line in processRunner.StreamLinesAsync(new ProcessCommand(
                           target.CliExecutable ?? "docker",
                           ["events", "--format", "{{json .}}"],
                           OutputMode: ProcessOutputMode.Lines), cancellationToken))
        {
            using JsonDocument doc = JsonDocument.Parse(line.Content);
            JsonElement root = doc.RootElement;
            yield return new ContainerEvent(
                root.GetProperty("Type").GetString() ?? string.Empty,
                root.GetProperty("Action").GetString() ?? string.Empty,
                root.GetProperty("Actor").GetProperty("ID").GetString() ?? string.Empty,
                line.Timestamp);
        }
    }

    public async IAsyncEnumerable<ProcessOutputLine> RunComposeAsync(
        ContainerRuntimeTarget target,
        ComposeCommand command,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "compose" };
        foreach (string file in command.ComposeFiles)
            args.AddRange(["-f", file]);
        if (!string.IsNullOrWhiteSpace(command.EnvironmentFile))
            args.AddRange(["--env-file", command.EnvironmentFile]);
        args.AddRange(command.Arguments);

        await foreach (ProcessOutputLine line in processRunner.StreamLinesAsync(new ProcessCommand(
                           target.CliExecutable ?? "docker",
                           args,
                           WorkingDirectory: command.WorkingDirectory,
                           Timeout: command.Timeout,
                           OutputMode: ProcessOutputMode.Lines), cancellationToken))
        {
            yield return line;
        }
    }

    private Task<ProcessResult> RunDockerAsync(ContainerRuntimeTarget target, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        processRunner.RunTextAsync(new ProcessCommand(
            target.CliExecutable ?? (target.Provider == ContainerRuntimeProvider.Podman ? "podman" : "docker"),
            args,
            Timeout: TimeSpan.FromMinutes(2)), cancellationToken);

    private static void ThrowIfFailed(ProcessResult result)
    {
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }
}
