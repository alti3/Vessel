using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.Docker;
using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.ValueObjects;

namespace Vessel.Infrastructure.Docker;

/// <summary>
///     Container runtime through the Docker/Podman CLI. Local targets run the CLI on the control-plane host; remote
///     targets (<see cref="ContainerRuntimeTarget.Remote" />, SSH servers) run the same structured CLI arguments on the
///     server over SSH through <see cref="IServerCommandRunner" /> (no <c>DOCKER_HOST=ssh://</c>, so the hardened SSH
///     options, pinned host key and team key apply to every runtime command).
///     Names, image references and paths are validated before they reach the CLI (SEC-10); build values are passed by
///     name and read from the CLI environment, never from the command line (DEP-09).
/// </summary>
public sealed partial class DockerCliContainerRuntimeClient(
    IProcessRunner processRunner,
    IServerCommandRunner? serverCommands = null) : IContainerRuntimeClient
{
    /// <summary>
    ///     Sources the build values file given as <c>$1</c> (exported), then executes the remaining arguments. The file
    ///     holds <c>KEY='value'</c> lines only, so sourcing expands nothing.
    /// </summary>
    public const string SourceBuildEnvironmentScript = "set -a; . \"$1\" || exit 1; set +a; shift; exec \"$@\"";

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
        if (!ContainerName.IsValid(containerId))
            throw new ArgumentException("Container name or id is invalid.", nameof(containerId));
        ProcessResult result = await RunDockerAsync(target, ["inspect", "--", containerId], cancellationToken);
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
        name = ContainerNetworkName.Parse(name).Value;
        ProcessResult inspect = await RunDockerAsync(target, ["network", "inspect", "--", name], cancellationToken);
        if (inspect.Succeeded) return;

        var args = new List<string> { "network", "create" };
        foreach (var (key, value) in labels)
            args.AddRange(["--label", Label(key, value)]);
        args.Add("--");
        args.Add(name);

        ProcessResult create = await RunDockerAsync(target, args, cancellationToken);
        ThrowIfFailed(create);
    }

    public async IAsyncEnumerable<ProcessOutputLine> BuildImageAsync(
        ContainerRuntimeTarget target,
        DockerBuildCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var image = ImageReference.Parse(command.ImageName).Value;
        if (command.DockerfilePath.StartsWith('-') || command.DockerfilePath.Any(char.IsControl))
            throw new ArgumentException("Dockerfile path is invalid.", nameof(command));
        var args = new List<string> { "build", "--pull", "--file", command.DockerfilePath, "--tag", image };
        if (command.NoCache) args.Add("--no-cache");
        foreach (var (key, value) in command.Labels)
            args.AddRange(["--label", Label(key, value)]);
        // Build arguments by name only: the values come from the CLI environment (DEP-09), never the command line.
        BuildEnvironment environment = Environment(command.BuildArguments.ToArray(), command.BuildArgumentsFile);
        foreach (var (key, _) in environment.Values)
            args.AddRange(["--build-arg", key]);
        args.Add(".");

        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.ResolveCliExecutable(), args,
                           command.WorkingDirectory, command.Timeout ?? TimeSpan.FromMinutes(30),
                           Redaction(command.Redaction, environment), cancellationToken, environment))
            yield return line;
    }

    public async IAsyncEnumerable<ProcessOutputLine> PrepareRailpackPlanAsync(
        ContainerRuntimeTarget target,
        RailpackPrepareCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireDocker(target, "Railpack");
        var image = ImageReference.Parse(command.CliImage).Value;
        RequirePath(command.ContextDirectory);
        RequirePath(command.OutputDirectory);
        if (!PlanFilePattern().IsMatch(command.PlanFileName))
            throw new ArgumentException("Railpack plan file name is invalid.", nameof(command));
        var args = new List<string>
        {
            "run", "--rm", "--pull", "missing",
            "--volume", $"{command.ContextDirectory}:/app:ro",
            "--volume", $"{command.OutputDirectory}:/out"
        };
        // `docker run --env NAME` copies the value from the CLI environment into the tool container.
        foreach (var (key, _) in command.Environment.Values) args.AddRange(["--env", ValidKey(key)]);
        args.AddRange([image, "prepare", "/app", "--plan-out", "/out/" + command.PlanFileName]);
        if (!string.IsNullOrWhiteSpace(command.BuildCommand)) args.AddRange(["--build-cmd", command.BuildCommand]);
        if (!string.IsNullOrWhiteSpace(command.StartCommand)) args.AddRange(["--start-cmd", command.StartCommand]);
        foreach (var (key, value) in command.ControlVariables) args.AddRange(["--env", $"{ValidKey(key)}={value}"]);
        // Railpack reads `--env NAME` (without a value) from its own environment and records it as a build secret.
        foreach (var (key, _) in command.Environment.Values)
            if (command.ControlVariables.All(control => control.Key != key))
                args.AddRange(["--env", key]);

        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.ResolveCliExecutable(), args, null,
                           command.Timeout ?? TimeSpan.FromMinutes(10),
                           Redaction(command.Redaction, command.Environment), cancellationToken, command.Environment))
            yield return line;
    }

    public async IAsyncEnumerable<ProcessOutputLine> BuildRailpackImageAsync(
        ContainerRuntimeTarget target,
        RailpackBuildCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireDocker(target, "Railpack");
        var image = ImageReference.Parse(command.ImageName).Value;
        var frontend = ImageReference.Parse(command.FrontendImage).Value;
        var buildKit = ImageReference.Parse(command.BuildKitImage).Value;
        var builder = ContainerName.Parse(command.BuilderName, "buildx builder name").Value;
        RequirePath(command.ContextDirectory);
        RequirePath(command.PlanFile);
        ProcessResult inspect = await RunDockerAsync(target, ["buildx", "inspect", builder], cancellationToken);
        if (!inspect.Succeeded)
        {
            ProcessResult create = await RunDockerAsync(target,
                ["buildx", "create", "--name", builder, "--driver", "docker-container", "--driver-opt",
                    "image=" + buildKit], cancellationToken);
            if (!create.Succeeded)
                throw new InvalidOperationException(
                    "Railpack builds require the Docker buildx plugin on the server; creating the BuildKit builder failed.");
        }

        var args = new List<string>
        {
            "buildx", "build", "--builder", builder, "--build-arg", "BUILDKIT_SYNTAX=" + frontend,
            "--file", command.PlanFile, "--progress", "plain", "--load", "--tag", image
        };
        if (command.NoCache) args.Add("--no-cache");
        else args.AddRange(["--build-arg", "cache-key=" + CacheKeyPattern().Replace(command.CacheKey, "_")]);
        if (!command.Environment.IsEmpty)
            args.AddRange(["--build-arg", "secrets-hash=" + SecretsHash(command.Environment)]);
        foreach (var (key, _) in command.Environment.Values)
            args.AddRange(["--secret", $"id={ValidKey(key)},env={key}"]);
        foreach (var (key, value) in command.Labels) args.AddRange(["--label", Label(key, value)]);
        args.Add(command.ContextDirectory);

        try
        {
            await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.ResolveCliExecutable(), args,
                               null, command.Timeout ?? TimeSpan.FromMinutes(45),
                               Redaction(command.Redaction, command.Environment), cancellationToken,
                               command.Environment))
                yield return line;
        }
        finally
        {
            // An idle BuildKit daemon keeps the memory of earlier builds (upstream v4.5.0 #12097). Callers serialize
            // Railpack builds per server, so no other build uses the builder now.
            await StopBuilderAsync(target, builder);
        }
    }

    private async Task StopBuilderAsync(ContainerRuntimeTarget target, string builder)
    {
        try
        {
            await RunDockerAsync(target, ["buildx", "stop", builder], CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Best effort.
        }
    }

    public async IAsyncEnumerable<ProcessOutputLine> GenerateNixpacksBuildAsync(
        ContainerRuntimeTarget target,
        NixpacksGenerateCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var image = ImageReference.Parse(command.ImageName).Value;
        RequirePath(command.ContextDirectory);
        var args = new List<string>
            { "build", command.ContextDirectory, "--out", command.ContextDirectory, "--name", image };
        if (!string.IsNullOrWhiteSpace(command.InstallCommand)) args.AddRange(["--install-cmd", command.InstallCommand]);
        if (!string.IsNullOrWhiteSpace(command.BuildCommand)) args.AddRange(["--build-cmd", command.BuildCommand]);
        if (!string.IsNullOrWhiteSpace(command.StartCommand)) args.AddRange(["--start-cmd", command.StartCommand]);
        // `--env NAME` makes Nixpacks read the value from its environment.
        foreach (var (key, _) in command.Environment.Values) args.AddRange(["--env", ValidKey(key)]);

        await foreach (ProcessOutputLine line in StreamDockerAsync(target, "nixpacks", args, null,
                           command.Timeout ?? TimeSpan.FromMinutes(10),
                           Redaction(command.Redaction, command.Environment), cancellationToken, command.Environment))
            yield return line;
    }

    public async Task<bool> TagImageAsync(ContainerRuntimeTarget target, string source, string destination,
        CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunDockerAsync(target,
            ["tag", ImageReference.Parse(source).Value, ImageReference.Parse(destination).Value], cancellationToken);
        return result.Succeeded;
    }

    public IAsyncEnumerable<ProcessOutputLine> PushImageAsync(ContainerRuntimeTarget target,
        RegistryImageCommand command, CancellationToken cancellationToken = default)
    {
        return RegistryAsync(target, "push", command, cancellationToken);
    }

    public IAsyncEnumerable<ProcessOutputLine> PullImageAsync(ContainerRuntimeTarget target,
        RegistryImageCommand command, CancellationToken cancellationToken = default)
    {
        return RegistryAsync(target, "pull", command, cancellationToken);
    }

    /// <summary>
    ///     Registry arguments with an isolated client configuration: <c>docker --config DIR verb ...</c> or
    ///     <c>podman verb --authfile DIR/auth.json ...</c>, so credentials never land in the daemon user's configuration.
    /// </summary>
    public static IReadOnlyList<string> RegistryArguments(ContainerRuntimeProvider provider, string configDirectory,
        params string[] args)
    {
        if (provider == ContainerRuntimeProvider.Podman)
            return [args[0], "--authfile", configDirectory.TrimEnd('/', '\\') + "/auth.json", .. args[1..]];
        return ["--config", configDirectory, .. args];
    }

    /// <summary>
    ///     Push or pull with an isolated client configuration: the password is sent on standard input to
    ///     <c>login --password-stdin</c> and removed again with <c>logout</c>.
    /// </summary>
    private async IAsyncEnumerable<ProcessOutputLine> RegistryAsync(ContainerRuntimeTarget target, string verb,
        RegistryImageCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var image = ImageReference.Parse(command.ImageReference).Value;
        RequirePath(command.ConfigDirectory);
        var secrets = new List<string>(command.Redaction?.SecretValues ?? []);
        RegistryAuth? auth = command.Auth;
        if (auth is not null)
        {
            if (!RegistryHostPattern().IsMatch(auth.Registry) || auth.Username.Length == 0 ||
                auth.Username.StartsWith('-') || auth.Username.Any(char.IsControl))
                throw new ArgumentException("Registry credentials are invalid.", nameof(command));
            secrets.Add(auth.Password);
            ProcessResult login = await RunDockerAsync(target,
                RegistryArguments(target.Provider, command.ConfigDirectory, "login", "--username", auth.Username,
                    "--password-stdin", auth.Registry), cancellationToken, TimeSpan.FromMinutes(2), auth.Password);
            if (!login.Succeeded)
                throw new InvalidOperationException($"Registry login to {auth.Registry} failed.");
        }

        try
        {
            await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.ResolveCliExecutable(),
                               RegistryArguments(target.Provider, command.ConfigDirectory, verb, image), null,
                               command.Timeout ?? TimeSpan.FromMinutes(30),
                               new ProcessRedactionProfile(secrets, command.Redaction?.PatternNames ?? [],
                                   command.Redaction?.KnownValues, command.Redaction?.IsLogOutput ?? false),
                               cancellationToken))
                yield return line;
        }
        finally
        {
            if (auth is not null) await LogoutAsync(target, command.ConfigDirectory, auth.Registry);
        }
    }

    private async Task LogoutAsync(ContainerRuntimeTarget target, string configDirectory, string registry)
    {
        try
        {
            await RunDockerAsync(target, RegistryArguments(target.Provider, configDirectory, "logout", registry),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // The isolated configuration is removed with the deployment workspace.
        }
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
            args.AddRange(["-f", RequirePath(file)]);
        if (!string.IsNullOrWhiteSpace(command.EnvironmentFile))
            args.AddRange(["--env-file", RequirePath(command.EnvironmentFile)]);
        args.AddRange(command.Arguments);

        // Compose reads user content: locally it starts from the allowlisted environment, so `${VAR}` in a repository
        // compose file never resolves against the control plane's process environment (SEC-10/SEC-11).
        await foreach (ProcessOutputLine line in StreamDockerAsync(target, target.CliExecutable ?? "docker", args,
                           command.WorkingDirectory, command.Timeout, command.Redaction, cancellationToken,
                           BuildEnvironment.Empty))
            yield return line;
    }

    public async Task<bool> RemoveComposeProjectAsync(
        ContainerRuntimeTarget target,
        string projectName,
        bool removeVolumes,
        CancellationToken cancellationToken = default)
    {
        projectName = ComposeProjectName.Validate(projectName);
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
        if (!ImageReference.TryParse(imageReference, out _) && !ImageIdPattern().IsMatch(imageReference))
            throw new ArgumentException("Image reference is invalid.", nameof(imageReference));
        ProcessResult result = await RunDockerAsync(target, ["image", "rm", "--", imageReference.Trim()],
            cancellationToken);
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
        CancellationToken cancellationToken, TimeSpan? commandTimeout = null, string? standardInput = null)
    {
        var timeout = commandTimeout ?? TimeSpan.FromMinutes(2);
        if (target.Remote is not { } remote)
        {
            var command = new ProcessCommand(target.ResolveCliExecutable(), args, Timeout: timeout);
            if (standardInput is null) return await processRunner.RunTextAsync(command, cancellationToken);
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(standardInput), false);
            return await processRunner.RunTextWithInputAsync(
                command with { Redaction = new ProcessRedactionProfile([standardInput], []) }, input,
                cancellationToken);
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        ServerCommandResult result = await RemoteRunner().RunAsync(remote, [target.ResolveCliExecutable(), .. args],
            timeout, standardInput, cancellationToken);
        var remoteCommand = new ProcessCommand("ssh", [target.ResolveCliExecutable(), .. args], Timeout: timeout);
        return new ProcessResult(remoteCommand, new ProcessExitInfo(result.ExitCode, false, false, null), 0, started,
            DateTimeOffset.UtcNow, result.StandardOutput, result.StandardError);
    }

    private IAsyncEnumerable<ProcessOutputLine> StreamDockerAsync(
        ContainerRuntimeTarget target,
        string executable,
        IReadOnlyList<string> args,
        string? workingDirectory,
        TimeSpan? timeout,
        ProcessRedactionProfile? redaction,
        CancellationToken cancellationToken,
        BuildEnvironment? environment = null)
    {
        if (target.Remote is { } remote)
            return RemoteRunner().StreamAsync(remote, RemoteArguments(executable, args, environment), workingDirectory,
                timeout ?? TimeSpan.FromHours(2), redaction?.SecretValues ?? [], cancellationToken);

        // Commands that process user content (compose, builds) start from an allowlisted environment (SEC-10).
        return processRunner.StreamLinesAsync(new ProcessCommand(
            executable,
            args,
            workingDirectory,
            environment is null ? null : DockerCliEnvironment.Build(environment),
            timeout,
            ProcessOutputMode.Lines,
            Redaction: redaction,
            InheritEnvironment: environment is null), cancellationToken);
    }

    /// <summary>
    ///     The argument vector run on an SSH server: with build values, a fixed wrapper sources the uploaded
    ///     <c>KEY='value'</c> file first, so values never appear on the remote command line.
    /// </summary>
    public static IReadOnlyList<string> RemoteArguments(string executable, IReadOnlyList<string> args,
        BuildEnvironment? environment)
    {
        if (environment is null || environment.IsEmpty) return [executable, .. args];
        var file = environment.ShellFilePath ?? throw new InvalidOperationException(
            "Build values for an SSH server need the uploaded build values file.");
        if (!file.StartsWith('/') || file.Any(char.IsControl))
            throw new InvalidOperationException("The build values file path must be absolute.");
        return ["sh", "-c", SourceBuildEnvironmentScript, "vessel-build-env", file, executable, .. args];
    }

    private static BuildEnvironment Environment(IReadOnlyList<KeyValuePair<string, string>> values, string? file)
    {
        foreach (var (key, _) in values) ValidKey(key);
        return new BuildEnvironment(values, file);
    }

    private static ProcessRedactionProfile Redaction(ProcessRedactionProfile? redaction, BuildEnvironment environment)
    {
        return new ProcessRedactionProfile(
            [.. environment.Values.Select(pair => pair.Value).Where(value => value.Length > 0), .. redaction?.SecretValues ?? []],
            redaction?.PatternNames ?? [], redaction?.KnownValues, redaction?.IsLogOutput ?? false);
    }

    private static string SecretsHash(BuildEnvironment environment)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in environment.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(key).Append('=').Append(value).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16]
            .ToLowerInvariant();
    }

    private static string ValidKey(string key)
    {
        if (!EnvironmentVariableKey.IsShellName(key))
            throw new ArgumentException($"'{key}' is not a valid build variable name.", nameof(key));
        return key;
    }

    private static string Label(string key, string value)
    {
        if (!LabelKeyPattern().IsMatch(key) || value.Any(char.IsControl))
            throw new ArgumentException("Container label is invalid.", nameof(key));
        return $"{key}={value}";
    }

    private static string RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-') || path.Any(char.IsControl))
            throw new ArgumentException("Path is invalid for a container runtime command.", nameof(path));
        return path;
    }

    private static void RequireDocker(ContainerRuntimeTarget target, string builder)
    {
        if (target.Provider != ContainerRuntimeProvider.Docker)
            throw new InvalidOperationException(
                $"{builder} builds need Docker with buildx; this server runs {target.Provider}. Use the Dockerfile build pack.");
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

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanFilePattern();

    [GeneratedRegex("^(sha256:)?[a-f0-9]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageIdPattern();

    [GeneratedRegex("[^A-Za-z0-9_.-]", RegexOptions.CultureInvariant)]
    private static partial Regex CacheKeyPattern();

    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9.-]*[a-zA-Z0-9])?(:[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryHostPattern();
}
