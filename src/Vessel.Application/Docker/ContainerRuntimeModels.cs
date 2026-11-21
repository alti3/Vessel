using Vessel.Application.Processes;
using Vessel.Application.Servers;
using Vessel.Domain.Servers;

namespace Vessel.Application.Docker;

public enum ContainerRuntimeProvider
{
    Docker,
    Podman
}

/// <summary>Where and how container runtime commands run for one server.</summary>
/// <param name="Provider">The runtime family (Docker or Podman).</param>
/// <param name="ApiEndpoint">Optional runtime API endpoint for local runtimes.</param>
/// <param name="CliExecutable">Optional runtime CLI override; defaults to the provider's CLI name.</param>
/// <param name="Remote">
///     Set for SSH servers: the runtime CLI runs on the remote host over SSH (through <c>IServerCommandRunner</c>), never
///     against the control-plane host's runtime.
/// </param>
public sealed record ContainerRuntimeTarget(
    ContainerRuntimeProvider Provider,
    Uri? ApiEndpoint = null,
    string? CliExecutable = null,
    ServerExecutionTarget? Remote = null)
{
    public bool IsRemote => Remote is not null;

    /// <summary>The runtime of a server, routed over its own connection (local runtime or SSH).</summary>
    public static ContainerRuntimeTarget For(Server server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var provider = server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker;
        return new ContainerRuntimeTarget(provider,
            Remote: server.ConnectionType == ServerConnectionType.Ssh ? ServerExecutionTarget.From(server) : null);
    }

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

/// <summary>A compose invocation. <paramref name="Redaction" /> carries the deployment's secret context for its output.</summary>
public sealed record ComposeCommand(
    string WorkingDirectory,
    IReadOnlyList<string> ComposeFiles,
    IReadOnlyList<string> Arguments,
    string? EnvironmentFile = null,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);

/// <summary>
///     An image build. <paramref name="BuildArguments" /> become <c>--build-arg KEY</c> (name only); their values reach
///     the CLI through its environment locally or through <paramref name="BuildArgumentsFile" /> (a
///     <see cref="BuildEnvironment" /> shell file) on SSH servers, so values never appear on a command line. Values are
///     always redacted from the output, plus anything in <paramref name="Redaction" />.
/// </summary>
public sealed record DockerBuildCommand(
    string WorkingDirectory,
    string DockerfilePath,
    string ImageName,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> BuildArguments,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null,
    string? BuildArgumentsFile = null,
    bool NoCache = false);

/// <summary>
///     Build values handed to the runtime CLI without putting them on a command line (DEP-09): locally they are set in
///     the CLI's process environment; on an SSH server they are read from <paramref name="ShellFilePath" />, a
///     <c>KEY='value'</c> file inside the uploaded deployment workspace that a fixed wrapper sources before it executes
///     the CLI. Keys are shell names that are not reserved for the container runtime client.
/// </summary>
public sealed record BuildEnvironment(
    IReadOnlyList<KeyValuePair<string, string>> Values,
    string? ShellFilePath = null)
{
    public static BuildEnvironment Empty { get; } = new([]);

    public bool IsEmpty => Values.Count == 0;
}

/// <summary>
///     <c>railpack prepare</c> in the pinned Railpack CLI image: the context is mounted read-only at <c>/app</c>, the
///     plan is written to <paramref name="OutputDirectory" />/<paramref name="PlanFileName" />. Build values reach the
///     tool through <paramref name="Environment" /> (<c>docker run -e KEY</c>, never <c>KEY=value</c>);
///     <paramref name="ControlVariables" /> (<c>RAILPACK_*</c>) are configuration and passed inline.
/// </summary>
public sealed record RailpackPrepareCommand(
    string ContextDirectory,
    string OutputDirectory,
    string PlanFileName,
    string CliImage,
    IReadOnlyList<KeyValuePair<string, string>> ControlVariables,
    BuildEnvironment Environment,
    string? BuildCommand = null,
    string? StartCommand = null,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);

/// <summary>
///     <c>docker buildx build</c> with the pinned Railpack BuildKit frontend on a dedicated <c>docker-container</c>
///     builder (pinned BuildKit image). Build values are BuildKit secrets (<c>--secret id=KEY,env=KEY</c>) read from
///     <paramref name="Environment" />. The builder is stopped after the build so an idle BuildKit daemon does not keep
///     memory (upstream v4.5.0 #12097).
/// </summary>
public sealed record RailpackBuildCommand(
    string ContextDirectory,
    string PlanFile,
    string ImageName,
    string FrontendImage,
    string BuildKitImage,
    string BuilderName,
    IReadOnlyDictionary<string, string> Labels,
    BuildEnvironment Environment,
    string CacheKey,
    bool NoCache = false,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);

/// <summary>
///     <c>nixpacks build --out</c> (deprecated build pack): generates <c>.nixpacks/Dockerfile</c> inside the context
///     without building; the image is then built with <see cref="DockerBuildCommand" /> and the same build values.
///     Needs the <c>nixpacks</c> CLI on the runtime host.
/// </summary>
public sealed record NixpacksGenerateCommand(
    string ContextDirectory,
    string ImageName,
    BuildEnvironment Environment,
    string? InstallCommand = null,
    string? BuildCommand = null,
    string? StartCommand = null,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);

/// <summary>Registry credentials resolved for one push or pull; the password is sent on standard input only.</summary>
public sealed record RegistryAuth(string Registry, string Username, string Password)
{
    public override string ToString()
    {
        return $"RegistryAuth {{ Registry = {Registry}, Username = {Username}, Password = *** }}";
    }
}

/// <summary>
///     A registry push or pull. With <paramref name="Auth" />, the client logs in with an isolated client configuration
///     in <paramref name="ConfigDirectory" /> (never the daemon user's <c>~/.docker</c>), so credentials do not outlive
///     the deployment workspace.
/// </summary>
public sealed record RegistryImageCommand(
    string ImageReference,
    RegistryAuth? Auth,
    string ConfigDirectory,
    TimeSpan? Timeout = null,
    ProcessRedactionProfile? Redaction = null);
