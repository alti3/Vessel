using System.Runtime.CompilerServices;
using System.Text.Json;
using Vessel.Application.Docker;
using Vessel.Application.Processes;
using Vessel.Application.Servers;

namespace Vessel.Infrastructure.Docker;

/// <summary>
///     Container runtime through the Docker/Podman CLI. Local targets run the CLI on the control-plane host; remote
///     targets (<see cref="ContainerRuntimeTarget.Remote" />, SSH servers) run the same structured CLI arguments on the
///     server over SSH through <see cref="IServerCommandRunner" /> (no <c>DOCKER_HOST=ssh://</c>, so the hardened SSH
///     options, pinned host key and team key apply to every runtime command).
/// </summary>
public sealed class DockerCliContainerRuntimeClient(
    IProcessRunner processRunner,
    IServerCommandRunner? serverCommands = null) : IContainerRuntimeClient
{
    public async Task<ContainerRuntimeInfo> GetInfoAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["version", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return ParseVersion(target.Provider, result.StandardOutput);
    }

    /// <summary>
    ///     Parses <c>docker version --format '{{json .}}'</c> (<c>ApiVersion</c>, <c>Os</c>, <c>Arch</c>) and the Podman
    ///     equivalent (<c>APIVersion</c>, <c>OsArch</c>). A missing server section means the daemon did not answer.
    /// </summary>
    public static ContainerRuntimeInfo ParseVersion(ContainerRuntimeProvider provider, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Server", out JsonElement server) ||
            server.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The container runtime daemon did not report a server version.");
        var osArch = Text(server, "OsArch");
        var os = Text(server, "Os");
        var arch = Text(server, "Arch");
        if (os.Length == 0 && osArch.Contains('/')) os = osArch[..osArch.IndexOf('/')];
        if (arch.Length == 0 && osArch.Contains('/')) arch = osArch[(osArch.IndexOf('/') + 1)..];
        var apiVersion = Text(server, "ApiVersion");
        if (apiVersion.Length == 0) apiVersion = Text(server, "APIVersion");
        return new ContainerRuntimeInfo(provider, Text(server, "Version"), apiVersion, os, arch);
    }

    /// <summary>Parses the comma-joined <c>key=value</c> label list of <c>docker ps --format '{{json .}}'</c>.</summary>
    public static IReadOnlyDictionary<string, string> ParseLabels(string? labels)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (labels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            parsed[pair[..separator].Trim()] = pair[(separator + 1)..];
        }

        return parsed;
    }

    private static string Text(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(ContainerRuntimeTarget target, bool all,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "ps", "--format", "{{json .}}" };
        if (all) args.Insert(1, "-a");
        ProcessResult result = await RunDockerAsync(target, args, cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new ContainerSummary(
                    root.GetProperty("ID").GetString() ?? string.Empty,
                    [root.GetProperty("Names").GetString() ?? string.Empty],
                    root.GetProperty("Image").GetString() ?? string.Empty,
                    root.TryGetProperty("State", out JsonElement state)
                        ? state.GetString() ?? string.Empty
                        : string.Empty,
                    root.TryGetProperty("Status", out JsonElement status)
                        ? status.GetString() ?? string.Empty
                        : string.Empty,
                    ParseLabels(root.TryGetProperty("Labels", out JsonElement labels) &&
                                labels.ValueKind == JsonValueKind.String
                        ? labels.GetString()
                        : null));
            })
            .ToArray();
    }

    public async Task<string> InspectContainerAsync(ContainerRuntimeTarget target, string containerId,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["inspect", containerId], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput;
    }

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target, ["images", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new ImageSummary(
                    root.GetProperty("ID").GetString() ?? string.Empty,
                    [$"{root.GetProperty("Repository").GetString()}:{root.GetProperty("Tag").GetString()}"],
                    0);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result =
            await RunDockerAsync(target, ["network", "ls", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new NetworkSummary(
                    root.GetProperty("ID").GetString() ?? string.Empty,
                    root.GetProperty("Name").GetString() ?? string.Empty,
                    root.GetProperty("Driver").GetString() ?? string.Empty);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<VolumeSummary>> ListVolumesAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result =
            await RunDockerAsync(target, ["volume", "ls", "--format", "{{json .}}"], cancellationToken);
        ThrowIfFailed(result);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                return new VolumeSummary(
                    root.GetProperty("Name").GetString() ?? string.Empty,
                    root.GetProperty("Driver").GetString() ?? string.Empty,
                    string.Empty);
            })
            .ToArray();
    }

    public async Task EnsureNetworkAsync(
        ContainerRuntimeTarget target,
        string name,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ProcessResult inspect = await RunDockerAsync(target, ["network", "inspect", name], cancellationToken);
        if (inspect.Succeeded) return;

        var args = new List<string> { "network", "create" };
        foreach (var (key, value) in labels)
            args.AddRange(["--label", $"{key}={value}"]);
        args.Add(name);

        ProcessResult create = await RunDockerAsync(target, args, cancellationToken);
        ThrowIfFailed(create);
    }

    public async IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(
        ContainerRuntimeTarget target,
        DockerBuildCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "build",
            "--pull",
            "--file",
            command.DockerfilePath,
            "--tag",
            command.ImageName
        };
        foreach (var (key, value) in command.Labels)
            args.AddRange(["--label", $"{key}={value}"]);
        foreach (var (key, value) in command.BuildArguments)
            args.AddRange(["--build-arg", $"{key}={value}"]);
        args.Add(".");

        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.ResolveCliExecutable(), args,
                           command.WorkingDirectory, command.Timeout ?? TimeSpan.FromMinutes(30),
                           new ProcessRedactionProfile(
                               [.. command.BuildArguments.Values, .. command.Redaction?.SecretValues ?? []],
                               command.Redaction?.PatternNames ?? [], command.Redaction?.KnownValues,
                               command.Redaction?.IsLogOutput ?? false),
                           cancellationToken))
            yield return line;
    }

    public async IAsyncEnumerable<ContainerEvent> StreamEventsAsync(
        ContainerRuntimeTarget target,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.CliExecutable ?? "docker",
                           ["events", "--format", "{{json .}}"], null, null, null, cancellationToken))
        {
            using var doc = JsonDocument.Parse(line.Content);
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "compose" };
        foreach (var file in command.ComposeFiles)
            args.AddRange(["-f", file]);
        if (!string.IsNullOrWhiteSpace(command.EnvironmentFile))
            args.AddRange(["--env-file", command.EnvironmentFile]);
        args.AddRange(command.Arguments);

        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.CliExecutable ?? "docker", args,
                           command.WorkingDirectory, command.Timeout, command.Redaction, cancellationToken))
            yield return line;
    }

    public async Task<bool> RemoveComposeProjectAsync(
        ContainerRuntimeTarget target,
        string projectName,
        bool removeVolumes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        var args = new List<string> { "compose", "--project-name", projectName, "down", "--remove-orphans" };
        if (removeVolumes) args.Add("--volumes");
        ProcessResult result = await RunDockerAsync(target, args, cancellationToken, TimeSpan.FromMinutes(5));
        return result.Succeeded;
    }

    public async Task<bool> RemoveImageAsync(
        ContainerRuntimeTarget target,
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        if (imageReference.StartsWith('-'))
            throw new ArgumentException("Image reference must not start with '-'.", nameof(imageReference));
        ProcessResult result = await RunDockerAsync(target, ["image", "rm", imageReference], cancellationToken);
        return result.Succeeded;
    }

    private async Task<ProcessResult> RunDockerAsync(ContainerRuntimeTarget target, IReadOnlyList<string> args,
        CancellationToken cancellationToken, TimeSpan? commandTimeout = null)
    {
        var timeout = commandTimeout ?? TimeSpan.FromMinutes(2);
        if (target.Remote is not { } remote)
            return await processRunner.RunTextAsync(new ProcessCommand(target.ResolveCliExecutable(), args,
                Timeout: timeout), cancellationToken);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        ServerCommandResult result = await RemoteRunner().RunAsync(remote, [target.ResolveCliExecutable(), .. args],
            timeout, null, cancellationToken);
        var command = new ProcessCommand("ssh", [target.ResolveCliExecutable(), .. args], Timeout: timeout);
        return new ProcessResult(command, new ProcessExitInfo(result.ExitCode, false, false, null), 0, started,
            DateTimeOffset.UtcNow, result.StandardOutput, result.StandardError);
    }

    private IAsyncEnumerable<ProcessOutputLine> StreamDockerAsync(
        ContainerRuntimeTarget target,
        string executable,
        IReadOnlyList<string> args,
        string? workingDirectory,
        TimeSpan? timeout,
        ProcessRedactionProfile? redaction,
        CancellationToken cancellationToken)
    {
        if (target.Remote is { } remote)
            return RemoteRunner().StreamAsync(remote, [executable, .. args], workingDirectory,
                timeout ?? TimeSpan.FromHours(2), redaction?.SecretValues ?? [], cancellationToken);

        return processRunner.StreamLinesAsync(new ProcessCommand(
            executable,
            args,
            workingDirectory,
            Timeout: timeout,
            OutputMode: ProcessOutputMode.Lines,
            Redaction: redaction), cancellationToken);
    }

    private IServerCommandRunner RemoteRunner()
    {
        return serverCommands ?? throw new InvalidOperationException(
            "Remote container runtime targets require the server command runner.");
    }

    private static void ThrowIfFailed(ProcessResult result)
    {
        if (!result.Succeeded) throw new ProcessExecutionException(result);
    }
}
