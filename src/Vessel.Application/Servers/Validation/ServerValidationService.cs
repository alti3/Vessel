using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Ssh;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Servers;

namespace Vessel.Application.Servers.Validation;

public sealed record ServerValidationOutcome(
    Guid ServerId,
    bool Succeeded,
    ServerStatus Status,
    string Message,
    string? OsId,
    string? OsVersion,
    string? RuntimeVersion,
    string? ComposeVersion,
    bool DockerInstalled,
    string? InstallGuidance,
    DateTimeOffset ValidatedAt);

/// <summary>
///     Real server validation (SRV-02): SSH reachability (pinning the host key on first use), operating system detection
///     from <c>/etc/os-release</c> (<c>ID</c>/<c>ID_LIKE</c>), login user and passwordless sudo, container runtime presence
///     and version (Docker 24+, Podman 4+), and Compose. Docker is installed only when the operator explicitly asks
///     (<c>installDocker</c>) and the login user is root or has passwordless sudo. Runs behind a per-server lock.
/// </summary>
public sealed class ServerValidationService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IServerCommandRunner commands,
    IContainerRuntimeClient runtime,
    IDistributedLockManager locks,
    ISecretRedactor redactor,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string ProbeScript =
        "for pm in apt-get dnf yum zypper apk pacman; do if command -v \"$pm\" >/dev/null 2>&1; then echo \"package_manager=$pm\"; break; fi; done; " +
        "if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then echo sudo=yes; else echo sudo=no; fi; " +
        "for c in docker podman curl wget; do if command -v \"$c\" >/dev/null 2>&1; then echo \"has_$c=yes\"; fi; done; " +
        "echo \"uid=$(id -u)\"";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

    public async Task<ServerValidationOutcome> ValidateAsync(
        ServerId serverId,
        bool installDocker,
        UserId? actorUserId,
        CancellationToken cancellationToken = default)
    {
        Server server = dbContext.Servers.SingleOrDefault(candidate => candidate.Id == serverId)
                        ?? throw new KeyNotFoundException("Server was not found.");
        if (!HostLocalExecutionGuard.IsAuthorized(authorization, server, actorUserId))
            return await RecordAsync(server, Failure(HostLocalExecutionGuard.UnattendedNotAuthorizedMessage),
                ServerStatus.Unknown, actorUserId, cancellationToken);

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            $"server-mutation:{serverId.Value:D}", TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5), cancellationToken);
        if (handle is null)
            throw new InvalidOperationException("Another operation is changing this server; try again shortly.");

        ServerExecutionTarget target = ServerExecutionTarget.From(server);
        Probe probe;
        try
        {
            probe = await ProbeAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await RecordAsync(server, Failure($"Could not connect to the server: {redactor.Redact(ex.Message)}"),
                ServerStatus.Unreachable, actorUserId, cancellationToken);
        }

        if (!probe.Connected)
            return await RecordAsync(server,
                Failure($"Could not connect to the server: {redactor.Redact(probe.Error ?? "unknown error")}"),
                ServerStatus.Unreachable, actorUserId, cancellationToken);

        var runtimeBinary = server.Runtime == ContainerRuntimeKind.Podman ? "podman" : "docker";
        var hasRuntime = probe.Facts.ContainsKey($"has_{runtimeBinary}");
        string? guidance = null;
        if (!hasRuntime)
        {
            guidance = ServerValidationRules.InstallGuidance(probe.PackageManager, server.Runtime);
            if (!installDocker || server.Runtime != ContainerRuntimeKind.Docker)
                return await RecordAsync(server,
                    Failure($"{(server.Runtime == ContainerRuntimeKind.Podman ? "Podman" : "Docker")} is not installed on the server.",
                        probe, guidance),
                    ServerStatus.Reachable, actorUserId, cancellationToken, guidance);

            string? installError = await InstallDockerAsync(target, probe, cancellationToken);
            if (installError is not null)
                return await RecordAsync(server, Failure(installError, probe, guidance), ServerStatus.Reachable,
                    actorUserId, cancellationToken, guidance);
        }

        ContainerRuntimeInfo info;
        try
        {
            info = await runtime.GetInfoAsync(ContainerRuntimeTarget.For(server), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var access = probe.IsRoot
                ? "Start the container runtime daemon on the server."
                : "The SSH user cannot reach the runtime daemon; add it to the 'docker' group or log in as root.";
            return await RecordAsync(server,
                Failure($"The container runtime of the server did not answer. {access}", probe, guidance),
                ServerStatus.Reachable, actorUserId, cancellationToken);
        }

        var versionError = ServerValidationRules.CheckRuntimeVersion(server.Runtime, info.Version);
        string? composeVersion = await ComposeVersionAsync(target, runtimeBinary, cancellationToken);
        var message = versionError
                      ?? (composeVersion is null
                          ? "The Compose plugin is missing; install docker-compose-plugin (or podman-compose)."
                          : null);
        var succeeded = message is null;
        var validation = ServerValidation.Create(succeeded, probe.Os.Id, probe.Os.IdLike, probe.Os.VersionId,
            probe.PackageManager, info.Version, composeVersion, probe.IsRoot, probe.HasSudo,
            message ?? ValidatedMessage(probe), timeProvider.GetUtcNow());
        if (succeeded)
            server.UpdateCapabilities(ServerCapability.Containers | ServerCapability.Compose |
                                      ServerCapability.Networks | ServerCapability.Volumes, timeProvider.GetUtcNow());
        return await RecordAsync(server, validation, ServerStatus.Reachable, actorUserId, cancellationToken);
    }

    private async Task<Probe> ProbeAsync(ServerExecutionTarget target, CancellationToken cancellationToken)
    {
        ServerCommandResult facts = await commands.RunAsync(target, ["sh", "-c", ProbeScript], ProbeTimeout, null,
            cancellationToken);
        if (!facts.Succeeded)
            return new Probe(false, facts.StandardError.Trim(), new Dictionary<string, string>(),
                new OsRelease(null, null, null, null));

        Dictionary<string, string> values = facts.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        ServerCommandResult osRelease = await commands.RunAsync(target, ["cat", "/etc/os-release"], ProbeTimeout, null,
            cancellationToken);
        OsRelease os = ServerValidationRules.ParseOsRelease(osRelease.Succeeded ? osRelease.StandardOutput : null);
        return new Probe(true, null, values, os);
    }

    private async Task<string?> InstallDockerAsync(ServerExecutionTarget target, Probe probe,
        CancellationToken cancellationToken)
    {
        if (!ServerValidationRules.IsSupported(probe.Os))
            return $"Vessel cannot install Docker on this operating system ({probe.Os.PrettyName ?? probe.Os.Id ?? "unknown"}).";
        var script = ServerValidationRules.DockerInstallScript(probe.PackageManager);
        if (script is null) return "Vessel cannot install Docker with the package manager of this server.";
        if (!probe.IsRoot && !probe.HasSudo)
            return "Installing Docker needs root: log in as root or give the SSH user passwordless sudo.";

        IReadOnlyList<string> command = probe.IsRoot
            ? ["sh", "-c", script]
            : ["sudo", "-n", "sh", "-c", script];
        ServerCommandResult result = await commands.RunAsync(target, command, InstallTimeout, null, cancellationToken);
        return result.Succeeded ? null : $"The Docker installation failed (exit code {result.ExitCode}).";
    }

    private async Task<string?> ComposeVersionAsync(ServerExecutionTarget target, string runtimeBinary,
        CancellationToken cancellationToken)
    {
        try
        {
            ServerCommandResult result = await commands.RunAsync(target,
                [runtimeBinary, "compose", "version", "--short"], ProbeTimeout, null, cancellationToken);
            var version = result.StandardOutput.Trim();
            return result.Succeeded && version.Length > 0 ? version.Split('\n')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<ServerValidationOutcome> RecordAsync(
        Server server,
        ServerValidation validation,
        ServerStatus status,
        UserId? actorUserId,
        CancellationToken cancellationToken,
        string? guidance = null)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        server.RecordValidation(validation, now);
        if (status != ServerStatus.Unknown) server.ChangeStatus(status, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(server.TeamId, actorUserId, AuditActions.ServerValidated,
            new AuditTarget("server", server.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["succeeded"] = validation.Succeeded,
                ["osId"] = validation.OsId,
                ["runtimeVersion"] = validation.RuntimeVersion,
                ["composeVersion"] = validation.ComposeVersion
            }, cancellationToken);
        return new ServerValidationOutcome(server.Id.Value, validation.Succeeded, server.Status,
            validation.Message ?? string.Empty, validation.OsId, validation.OsVersion, validation.RuntimeVersion,
            validation.ComposeVersion, validation.RuntimeVersion is not null, guidance, validation.ValidatedAt);
    }

    private ServerValidation Failure(string message, Probe? probe = null, string? guidance = null)
    {
        var text = guidance is null ? message : $"{message} {guidance}";
        return ServerValidation.Create(false, probe?.Os.Id, probe?.Os.IdLike, probe?.Os.VersionId,
            probe?.PackageManager, null, null, probe?.IsRoot ?? false, probe?.HasSudo ?? false, text,
            timeProvider.GetUtcNow());
    }

    private static string ValidatedMessage(Probe probe)
    {
        var os = probe.Os.PrettyName ?? probe.Os.Id ?? "unknown OS";
        var supported = ServerValidationRules.IsSupported(probe.Os) ? string.Empty : " (unrecognized OS family)";
        var http = probe.Facts.ContainsKey("has_curl") || probe.Facts.ContainsKey("has_wget")
            ? string.Empty
            : " Install curl or wget for deployment health checks.";
        return $"Server validated on {os}{supported}.{http}";
    }

    private sealed record Probe(bool Connected, string? Error, IReadOnlyDictionary<string, string> Facts, OsRelease Os)
    {
        public string? PackageManager => Facts.TryGetValue("package_manager", out var value) ? value : null;

        public bool IsRoot => Facts.TryGetValue("uid", out var uid) && uid == "0";

        public bool HasSudo => Facts.TryGetValue("sudo", out var sudo) && sudo == "yes";
    }
}

/// <summary>Hangfire entry point for <see cref="ServerValidationService" />.</summary>
public sealed class ValidateServerJob(ServerValidationService validation)
{
    public Task RunAsync(Guid serverId, bool installDocker, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        return validation.ValidateAsync(new ServerId(serverId), installDocker,
            actorUserId.HasValue ? new UserId(actorUserId.Value) : null, cancellationToken);
    }
}
