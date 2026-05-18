namespace Vessel.Application.Docker;

public enum ContainerRuntimeProvider
{
    Docker,
    Podman
}

public sealed record ContainerRuntimeTarget(
    ContainerRuntimeProvider Provider,
    Uri? ApiEndpoint = null,
    string? CliExecutable = null);

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
