using System.Globalization;
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

    /// <summary>Lists every container id, then inspects them with a template that never reads env or commands.</summary>
    public const string InspectAllScript =
        "ids=$(\"$1\" ps -aq --no-trunc) || exit 1; [ -z \"$ids\" ] && exit 0; \"$1\" inspect --format \"$2\" $ids";

    public const string ImageDetailsFormat =
        "{\"id\":{{json .Id}},\"tags\":{{json .RepoTags}},\"size\":{{.Size}},\"created\":{{json .Created}}," +
        "\"labels\":{{json .Config.Labels}}}";

    public const string InspectAllImagesScript =
        "ids=$(\"$1\" images -aq --no-trunc | sort -u) || exit 1; [ -z \"$ids\" ] && exit 0; " +
        "\"$1\" image inspect --format \"$2\" $ids";

    public async Task<IReadOnlyList<ContainerStateReading>> ListContainerStatesAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunShellAsync(target, InspectAllScript,
            [target.ResolveCliExecutable(), Monitoring.AgentlessTelemetryCollector.InspectFormat], cancellationToken);
        // A container removed between listing and inspecting makes inspect exit 1 with the others still printed.
        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StandardOutput)) ThrowIfFailed(result);
        return Monitoring.TelemetryParsers.ParseContainers(result.StandardOutput);
    }

    public async Task<IReadOnlyList<ImageDetails>> ListImageDetailsAsync(ContainerRuntimeTarget target,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunShellAsync(target, InspectAllImagesScript,
            [target.ResolveCliExecutable(), ImageDetailsFormat], cancellationToken);
        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StandardOutput)) ThrowIfFailed(result);
        return ParseImageDetails(result.StandardOutput);
    }

    public static IReadOnlyList<ImageDetails> ParseImageDetails(string output)
    {
        var images = new Dictionary<string, ImageDetails>(StringComparer.Ordinal);
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                                                    StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                var id = Text(root, "id");
                if (id.Length == 0) continue;
                var tags = root.TryGetProperty("tags", out JsonElement tagElement) &&
                           tagElement.ValueKind == JsonValueKind.Array
                    ? tagElement.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String)
                        .Select(tag => tag.GetString() ?? string.Empty).Where(tag => tag.Length > 0).ToArray()
                    : [];
                var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("labels", out JsonElement labelElement) &&
                    labelElement.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty label in labelElement.EnumerateObject())
                        if (label.Value.ValueKind == JsonValueKind.String)
                            labels[label.Name] = label.Value.GetString() ?? string.Empty;
                var size = root.TryGetProperty("size", out JsonElement sizeElement) &&
                           sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt64(out var bytes)
                    ? bytes
                    : 0;
                images[id] = new ImageDetails(id, tags, size,
                    Monitoring.TelemetryParsers.Timestamp(Text(root, "created")), labels);
            }
            catch (JsonException)
            {
                // Skipped: an image removed while it was being inspected.
            }
        }

        return images.Values.ToArray();
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamContainerLogsAsync(ContainerRuntimeTarget target,
        string containerId, ContainerLogOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireIdentifier(containerId, nameof(containerId));
        var args = new List<string> { "logs", "--timestamps" };
        if (options.Tail is { } tail) args.AddRange(["--tail", Math.Max(0, tail).ToString(CultureInfo.InvariantCulture)]);
        if (options.Since is { } since)
            args.AddRange(["--since", since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture)]);
        if (options.Follow) args.Add("--follow");
        args.Add(containerId);
        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.ResolveCliExecutable(), args, null,
                           options.Follow ? TimeSpan.FromHours(8) : TimeSpan.FromMinutes(2), null, cancellationToken))
            yield return line;
    }

    public async Task<bool> StopContainersAsync(ContainerRuntimeTarget target, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        if (containerIds.Count == 0) return true;
        foreach (var id in containerIds) RequireIdentifier(id, nameof(containerIds));
        ProcessResult result = await RunDockerAsync(target, ["stop", "--time", "30", .. containerIds],
            cancellationToken, TimeSpan.FromMinutes(3));
        return result.Succeeded;
    }

    public async Task<int> RemoveContainersAsync(ContainerRuntimeTarget target, IReadOnlyList<string> containerIds,
        CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var id in containerIds)
        {
            RequireIdentifier(id, nameof(containerIds));
            // Never forced (a running container stays) and never with -v (volumes stay).
            ProcessResult result = await RunDockerAsync(target, ["rm", id], cancellationToken);
            if (result.Succeeded) removed++;
        }

        return removed;
    }

    public async Task<RuntimePruneResult> PruneAsync(ContainerRuntimeTarget target, RuntimePruneKind kind,
        CancellationToken cancellationToken = default)
    {
        var podman = target.Provider == ContainerRuntimeProvider.Podman;
        string[]? args = kind switch
        {
            RuntimePruneKind.DanglingImages => ["image", "prune", "--force"],
            RuntimePruneKind.BuildCache => podman ? null : ["builder", "prune", "--all", "--force"],
            RuntimePruneKind.Networks => ["network", "prune", "--force"],
            // Docker 23+ prunes only anonymous volumes without --all; Podman would prune named volumes too.
            RuntimePruneKind.AnonymousVolumes => podman ? null : ["volume", "prune", "--force"],
            _ => null
        };
        if (args is null) return new RuntimePruneResult(true, 0, false);
        ProcessResult result = await RunDockerAsync(target, args, cancellationToken, TimeSpan.FromMinutes(9));
        return new RuntimePruneResult(result.Succeeded,
            Monitoring.TelemetryParsers.ParseReclaimedSpace(result.StandardOutput));
    }

    private static void RequireIdentifier(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.StartsWith('-') || value.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                                                              character is '_' or '.' or '-')))
            throw new ArgumentException("Container identifier contains unsupported characters.", name);
    }

    private async Task<ProcessResult> RunShellAsync(ContainerRuntimeTarget target, string script,
        IReadOnlyList<string> positional, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(2);
        string[] arguments = ["-c", script, "vessel", .. positional];
        if (target.Remote is not { } remote)
            return await processRunner.RunTextAsync(new ProcessCommand("sh", arguments, Timeout: timeout),
                cancellationToken);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        ServerCommandResult result = await RemoteRunner().RunAsync(remote, ["sh", .. arguments], timeout, null,
            cancellationToken);
        var command = new ProcessCommand("ssh", ["sh", .. arguments], Timeout: timeout);
        return new ProcessResult(command, new ProcessExitInfo(result.ExitCode, false, false, null), 0, started,
            DateTimeOffset.UtcNow, result.StandardOutput, result.StandardError);
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
