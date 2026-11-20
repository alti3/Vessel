namespace Vessel.Application.Docker;

public enum ContainerRuntimeProvider
{
    Docker,
    Podman
}

public sealed record ContainerRuntimeTarget(
    ContainerRuntimeProvider Provider,
    Uri? ApiEndpoint = null,
    string? CliExecutable = null)
{
    /// <summary>
    /// The runtime CLI used for this target: an explicit <see cref="CliExecutable" />, otherwise the provider's
    /// default binary (<c>docker</c> or <c>podman</c>).
    /// </summary>
    public string ResolveCliExecutable()
    {
        if (!string.IsNullOrWhiteSpace(CliExecutable)) return CliExecutable;
        return Provider == ContainerRuntimeProvider.Podman ? "podman" : "docker";
    }
}

public sealed record ContainerRuntimeInfo(
    ContainerRuntimeProvider Provider,
    string Version,
    string ApiVersion,
    string OperatingSystem,
    string Architecture);

public sealed record ContainerSummary(
    string Id,
    IReadOnlyList<string> Names,
    string Image,
    string State,
    string Status,
    IReadOnlyDictionary<string, string> Labels);

public sealed record ImageSummary(string Id, IReadOnlyList<string> RepoTags, long Size);

public sealed record NetworkSummary(string Id, string Name, string Driver);

public sealed record VolumeSummary(string Name, string Driver, string Mountpoint);

public sealed record ContainerEvent(string Type, string Action, string ActorId, DateTimeOffset Timestamp);

public sealed record ComposeCommand(
    string WorkingDirectory,
    IReadOnlyList<string> ComposeFiles,
    IReadOnlyList<string> Arguments,
    string? EnvironmentFile = null,
    TimeSpan? Timeout = null);

public sealed record DockerBuildCommand(
    string WorkingDirectory,
    string DockerfilePath,
    string ImageName,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> BuildArguments,
    TimeSpan? Timeout = null);
