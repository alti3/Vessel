using System.Text.Json;
using System.Text.RegularExpressions;
using Vessel.Application.Proxy;
using Vessel.Application.Servers;
using Vessel.Domain.Servers;
using Vessel.Infrastructure.Servers;

namespace Vessel.Infrastructure.Proxy;

/// <summary>
///     Proxy container control through the Docker/Podman CLI on the server's runtime host (local or over SSH).
///     The proxy container itself mounts no Docker socket; it only reads Vessel's file-provider directory.
/// </summary>
public sealed partial class DockerProxyContainerRuntime(ServerCommandExecutor executor) : IProxyContainerRuntime
{
    public const string InspectFormat =
        "{\"state\":{{json .State}},\"image\":{{json .Config.Image}},\"networks\":{{json .NetworkSettings.Networks}}}";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(10);

    public async Task<ProxyContainerStatus> InspectAsync(
        ServerExecutionTarget target,
        CancellationToken cancellationToken = default)
    {
        ServerCommandResult result = await executor.RunAsync(target,
            [Cli(target), "container", "inspect", "--format", InspectFormat, ProxyNaming.ProxyContainerName],
            ShortTimeout, null, cancellationToken);
        if (!result.Succeeded)
        {
            if (IsNoSuchObject(result))
                return ProxyContainerStatus.Missing;
            throw Failure("inspect the proxy container", result);
        }

        return ParseInspect(result.StandardOutput);
    }

    public async Task EnsureNetworkAsync(
        ServerExecutionTarget target,
        string network,
        CancellationToken cancellationToken = default)
    {
        ValidateNetwork(network);
        ServerCommandResult inspect = await executor.RunAsync(target,
            [Cli(target), "network", "inspect", "--format", "{{.Name}}", network], ShortTimeout, null,
            cancellationToken);
        if (inspect.Succeeded)
            return;

        ServerCommandResult create = await executor.RunAsync(target,
            [Cli(target), "network", "create", "--label", "vessel.managed=true", network], ShortTimeout, null,
            cancellationToken);
        if (!create.Succeeded && !Mentions(create, "already exists"))
            throw Failure($"create network '{network}'", create);
    }

    public async Task UpAsync(
        ServerExecutionTarget target,
        string composeHostPath,
        bool forceRecreate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeHostPath);
        if (!Path.IsPathRooted(composeHostPath) && !composeHostPath.StartsWith('/'))
            throw new ArgumentException("Compose file path must be absolute.", nameof(composeHostPath));

        var args = new List<string>
        {
            Cli(target), "compose", "--project-name", ProxyNaming.ProxyComposeProject, "--file", composeHostPath,
            "up", "--detach", "--remove-orphans"
        };
        if (forceRecreate)
            args.Add("--force-recreate");
        if (target.Runtime == ContainerRuntimeKind.Docker)
            args.AddRange(["--wait", "--wait-timeout", "120"]);

        ServerCommandResult result = await executor.RunAsync(target, args, UpTimeout, null, cancellationToken);
        if (!result.Succeeded)
            throw Failure("start the proxy", result);
    }

    public async Task StopAsync(ServerExecutionTarget target, CancellationToken cancellationToken = default)
    {
        ServerCommandResult stop = await executor.RunAsync(target,
            [Cli(target), "container", "stop", "--time", "30", ProxyNaming.ProxyContainerName], ShortTimeout, null,
            cancellationToken);
        if (!stop.Succeeded && !IsNoSuchObject(stop))
            throw Failure("stop the proxy", stop);

        ServerCommandResult remove = await executor.RunAsync(target,
            [Cli(target), "container", "rm", "--force", ProxyNaming.ProxyContainerName], ShortTimeout, null,
            cancellationToken);
        if (!remove.Succeeded && !IsNoSuchObject(remove))
            throw Failure("remove the proxy container", remove);
    }

    public async Task ConnectNetworkAsync(
        ServerExecutionTarget target,
        string network,
        CancellationToken cancellationToken = default)
    {
        ValidateNetwork(network);
        ServerCommandResult result = await executor.RunAsync(target,
            [Cli(target), "network", "connect", network, ProxyNaming.ProxyContainerName], ShortTimeout, null,
            cancellationToken);
        if (!result.Succeeded && !Mentions(result, "already exists") && !Mentions(result, "already connected"))
            throw Failure($"connect the proxy to network '{network}'", result);
    }

    public static ProxyContainerStatus ParseInspect(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null)
            return new ProxyContainerStatus(ProxyContainerState.Unknown, null, null, []);

        using var document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        ProxyContainerState state = ProxyContainerState.Unknown;
        string? health = null;
        if (root.TryGetProperty("state", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.Object)
        {
            if (stateElement.TryGetProperty("Status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.String)
                state = ParseState(status.GetString());
            if (stateElement.TryGetProperty("Health", out JsonElement healthElement) &&
                healthElement.ValueKind == JsonValueKind.Object &&
                healthElement.TryGetProperty("Status", out JsonElement healthStatus) &&
                healthStatus.ValueKind == JsonValueKind.String)
                health = healthStatus.GetString();
        }

        var image = root.TryGetProperty("image", out JsonElement imageElement) &&
                    imageElement.ValueKind == JsonValueKind.String
            ? imageElement.GetString()
            : null;
        string[] networks = root.TryGetProperty("networks", out JsonElement networksElement) &&
                            networksElement.ValueKind == JsonValueKind.Object
            ? networksElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        return new ProxyContainerStatus(state, string.IsNullOrWhiteSpace(health) ? null : health, image, networks);
    }

    private static ProxyContainerState ParseState(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "running" => ProxyContainerState.Running,
            "created" or "configured" or "initialized" => ProxyContainerState.Created,
            "restarting" => ProxyContainerState.Restarting,
            "paused" => ProxyContainerState.Paused,
            "exited" or "stopped" or "removing" => ProxyContainerState.Exited,
            "dead" => ProxyContainerState.Dead,
            _ => ProxyContainerState.Unknown
        };
    }

    private static string Cli(ServerExecutionTarget target)
    {
        return target.Runtime == ContainerRuntimeKind.Podman ? "podman" : "docker";
    }

    private static void ValidateNetwork(string network)
    {
        if (string.IsNullOrWhiteSpace(network) || !NetworkPattern().IsMatch(network))
            throw new ArgumentException("Network name is invalid.", nameof(network));
    }

    private static bool IsNoSuchObject(ServerCommandResult result)
    {
        return Mentions(result, "no such container") || Mentions(result, "no such object") ||
               Mentions(result, "no container with name");
    }

    private static bool Mentions(ServerCommandResult result, string text)
    {
        return result.StandardError.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               result.StandardOutput.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException Failure(string operation, ServerCommandResult result)
    {
        var detail = result.StandardError.Trim();
        if (detail.Length > 1000)
            detail = detail[..1000];
        return new InvalidOperationException(detail.Length == 0
            ? $"Could not {operation} (exit code {result.ExitCode})."
            : $"Could not {operation} (exit code {result.ExitCode}): {detail}");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkPattern();
}
