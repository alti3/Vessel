using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Common;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Proxy;
using Vessel.Domain.Servers;

namespace Vessel.Application.Proxy;

/// <summary>
///     Per-server proxy lifecycle: settings, static configuration delivery, container start/stop/restart, status, and
///     attaching the proxy to the server network for deployments. Every mutation runs under the
///     <c>proxy:{serverId}</c> distributed lock shared with <see cref="ProxyConfigurationService" />.
/// </summary>
public sealed class ProxyLifecycleService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IDistributedLockManager locks,
    IEnumerable<IServerFileChannel> fileChannels,
    IProxyContainerRuntime runtime,
    IProxyRuntimeRenderer renderer,
    IAuditWriter auditWriter,
    ISecretRedactor redactor,
    TimeProvider timeProvider) : IProxyStatusProbe
{
    public static readonly TimeSpan LockLease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeploymentLockWait = TimeSpan.FromSeconds(60);

    private readonly Dictionary<ServerId, ServerProxySettings> _pendingSettings = [];

    public static string LockKey(ServerId serverId)
    {
        return $"proxy:{serverId.Value:D}";
    }

    public async Task<ServerProxySummary> GetAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersRead);
        Server server = LoadServer(serverId);
        ServerProxySettings settings = FindSettings(serverId) ?? DefaultSettings(serverId);
        return await SummarizeAsync(server, settings, cancellationToken);
    }

    public async Task<ServerProxySummary> UpdateAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        UpdateServerProxySettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        Server server = LoadServer(serverId);
        // Managing the proxy of a Local/DockerSocket server runs Docker on the control-plane host.
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        ServerProxyType? requestedType = ParseProxyType(request.ProxyType);

        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        ServerProxySettings settings = await GetOrCreateSettingsAsync(serverId, cancellationToken);
        ServerProxyType previousType = settings.ProxyType;
        var previousHttp = settings.HttpPort;
        var previousHttps = settings.HttpsPort;
        ServerProxyType nextType = requestedType ?? settings.ProxyType;

        if (nextType != previousType && settings.RoutingEnabled)
        {
            // Switching provider while the old container still holds 80/443 would strand it (upstream requires a stop).
            ProxyContainerStatus status = await InspectSafeAsync(ServerExecutionTarget.From(server), cancellationToken);
            if (status.State is not (ProxyContainerState.Missing or ProxyContainerState.Exited
                or ProxyContainerState.Created or ProxyContainerState.Dead))
                throw new DomainException("Stop the proxy before changing the proxy type.");
        }

        settings.Configure(nextType, request.HttpPort ?? settings.HttpPort, request.HttpsPort ?? settings.HttpsPort,
            timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        var changes = new Dictionary<string, object?>();
        if (previousType != settings.ProxyType)
            changes["proxyType"] = new { from = previousType.ToString(), to = settings.ProxyType.ToString() };
        if (previousHttp != settings.HttpPort)
            changes["httpPort"] = new { from = previousHttp, to = settings.HttpPort };
        if (previousHttps != settings.HttpsPort)
            changes["httpsPort"] = new { from = previousHttps, to = settings.HttpsPort };
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.ServerProxyUpdated,
            ServerTarget(serverId), null, changes, cancellationToken);

        return await SummarizeAsync(server, settings, cancellationToken);
    }

    public Task<ServerProxySummary> StartAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        return RunOperatorActionAsync(actorUserId, teamId, serverId, ProxyAction.Start, cancellationToken);
    }

    public Task<ServerProxySummary> StopAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        return RunOperatorActionAsync(actorUserId, teamId, serverId, ProxyAction.Stop, cancellationToken);
    }

    public Task<ServerProxySummary> RestartAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        return RunOperatorActionAsync(actorUserId, teamId, serverId, ProxyAction.Restart, cancellationToken);
    }

    /// <summary>
    ///     Called by the deployment runner after the server network exists and before application containers start.
    ///     Starts the proxy when the operator has not stopped it and attaches it to <paramref name="network" />.
    ///     Never throws for proxy failures; the result is logged by the caller (upstream ignores these errors too).
    /// </summary>
    public async Task<ProxyEnsureResult> EnsureForDeploymentAsync(
        ServerId serverId,
        string network,
        CancellationToken cancellationToken = default)
    {
        Server server = LoadServer(serverId);
        ServerProxySettings effective = FindSettings(serverId) ?? DefaultSettings(serverId);
        if (!effective.RoutingEnabled)
            return new ProxyEnsureResult(true, true, "Server proxy type is None; skipping proxy attachment.");
        if (effective.DesiredState == ProxyDesiredState.Stopped)
            return new ProxyEnsureResult(true, true,
                "The server proxy was stopped by an operator; it is not started for this deployment.");

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(LockKey(serverId), LockLease,
            DeploymentLockWait, cancellationToken);
        if (handle is null)
            return new ProxyEnsureResult(false, false,
                "Timed out waiting for the proxy lock; the proxy was not attached to the deployment network.");

        ServerProxySettings settings = await GetOrCreateSettingsAsync(serverId, cancellationToken);
        var target = ServerExecutionTarget.From(server);
        try
        {
            ProxyContainerStatus status = await runtime.InspectAsync(target, cancellationToken);
            var started = false;
            if (status.State != ProxyContainerState.Running)
            {
                await StartCoreAsync(target, settings, false, cancellationToken);
                started = true;
            }

            await runtime.ConnectNetworkAsync(target, network, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new ProxyEnsureResult(true, false, started
                ? $"Started the server proxy and attached it to network '{network}'."
                : $"Server proxy is running and attached to network '{network}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = redactor.Redact(ex.Message);
            settings.RecordFailure(message, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return new ProxyEnsureResult(false, false, $"Server proxy could not be ensured: {message}");
        }
    }

    /// <summary>Routing state for <see cref="ProxyConfigurationService" />; servers without a row default to Traefik.</summary>
    public bool IsRoutingEnabled(ServerId serverId)
    {
        return (FindSettings(serverId) ?? DefaultSettings(serverId)).RoutingEnabled;
    }

    /// <summary>
    ///     Writes the dynamic (routes) document to the server's file-provider directory atomically and verifies it by
    ///     reading it back. Traefik reloads it through <c>providers.file.watch</c>; no signal is sent. The caller must
    ///     hold the proxy lock and save changes.
    /// </summary>
    public async Task<ProxyApplyResult> DeliverDynamicConfigurationAsync(
        Server server,
        ProxyConfigurationDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(document);
        var target = ServerExecutionTarget.From(server);
        try
        {
            IServerFileChannel channel = fileChannels.For(target);
            await channel.EnsureDirectoryAsync(target, ProxyLayout.Directory, ServerFileModes.WorldReadableDirectory,
                cancellationToken);
            await channel.EnsureDirectoryAsync(target, ProxyLayout.DynamicDirectory,
                ServerFileModes.WorldReadableDirectory, cancellationToken);
            await WriteVerifiedAsync(target, channel,
                new ServerFileWrite(ProxyLayout.DynamicConfigurationFile, document.Contents,
                    ServerFileModes.OwnerReadWrite), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProxyApplyResult(false, $"Could not deliver the dynamic proxy configuration: {ex.Message}");
        }

        ServerProxySettings settings = await GetOrCreateSettingsAsync(server.Id, cancellationToken);
        settings.RecordDynamicApplied(document.Sha256Hash, timeProvider.GetUtcNow());
        ProxyContainerStatus status = await InspectSafeAsync(target, cancellationToken);
        return status.State == ProxyContainerState.Running
            ? new ProxyApplyResult(true,
                "Dynamic configuration delivered and verified; Traefik reloads it through the file provider watch.")
            : new ProxyApplyResult(true,
                $"Dynamic configuration delivered and verified; the proxy container is not running (state: {StateName(status.State)}).");
    }

    /// <summary>
    ///     Writes (and verifies) a user dynamic configuration file next to the generated routes. The caller must hold the
    ///     proxy lock and must have validated the file name and contents.
    /// </summary>
    public async Task<ProxyApplyResult> DeliverUserDynamicConfigurationAsync(
        Server server,
        string fileName,
        string contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var target = ServerExecutionTarget.From(server);
        try
        {
            IServerFileChannel channel = fileChannels.For(target);
            await channel.EnsureDirectoryAsync(target, ProxyLayout.Directory, ServerFileModes.WorldReadableDirectory,
                cancellationToken);
            await channel.EnsureDirectoryAsync(target, ProxyLayout.DynamicDirectory,
                ServerFileModes.WorldReadableDirectory, cancellationToken);
            await WriteVerifiedAsync(target, channel,
                new ServerFileWrite(ProxyLayout.DynamicFile(fileName), contents, ServerFileModes.OwnerReadWrite),
                cancellationToken);
            return new ProxyApplyResult(true,
                "Dynamic configuration file delivered and verified; Traefik reloads it through the file provider watch.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProxyApplyResult(false, $"Could not deliver the dynamic configuration file: {ex.Message}");
        }
    }

    /// <summary>Removes a user dynamic configuration file. The caller must hold the proxy lock.</summary>
    public async Task<ProxyApplyResult> RemoveUserDynamicConfigurationAsync(
        Server server,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var target = ServerExecutionTarget.From(server);
        try
        {
            var removed = await fileChannels.For(target)
                .DeleteFileAsync(target, ProxyLayout.DynamicFile(fileName), cancellationToken);
            return new ProxyApplyResult(true,
                removed ? "Dynamic configuration file removed." : "Dynamic configuration file was already absent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProxyApplyResult(false, $"Could not remove the dynamic configuration file: {ex.Message}");
        }
    }

    public async Task<bool> IsHealthyAsync(Server server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ServerProxySettings settings = FindSettings(server.Id) ?? DefaultSettings(server.Id);
        if (!settings.RoutingEnabled)
            return true;
        try
        {
            ProxyContainerStatus status =
                await runtime.InspectAsync(ServerExecutionTarget.From(server), cancellationToken);
            return status.IsHealthy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<ServerProxySummary> RunOperatorActionAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        ProxyAction action,
        CancellationToken cancellationToken)
    {
        RequireServer(actorUserId, teamId, serverId, VesselPermissions.ServersWrite);
        Server server = LoadServer(serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        if (server.Status == ServerStatus.Unreachable)
            throw new DomainException("Cannot change the proxy while the server is unreachable.");

        await using DistributedLockHandle handle = await AcquireAsync(serverId, TimeSpan.Zero, cancellationToken);
        ServerProxySettings settings = await GetOrCreateSettingsAsync(serverId, cancellationToken);
        if (action != ProxyAction.Stop && !settings.RoutingEnabled)
            throw new DomainException("Server proxy type is None; there is no proxy to start.");

        var target = ServerExecutionTarget.From(server);
        try
        {
            if (action == ProxyAction.Stop)
            {
                await runtime.StopAsync(target, cancellationToken);
                settings.MarkStopped(timeProvider.GetUtcNow());
            }
            else
            {
                await StartCoreAsync(target, settings, action == ProxyAction.Restart, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = redactor.Redact(ex.Message);
            settings.RecordFailure(message, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw new DomainException($"Proxy {action.ToString().ToLowerInvariant()} failed: {message}");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var auditAction = action switch
        {
            ProxyAction.Start => AuditActions.ServerProxyStarted,
            ProxyAction.Stop => AuditActions.ServerProxyStopped,
            _ => AuditActions.ServerProxyRestarted
        };
        await auditWriter.RecordAsync(teamId, actorUserId, auditAction, ServerTarget(serverId), null,
            new Dictionary<string, object?>
            {
                ["proxyType"] = settings.ProxyType.ToString(),
                ["image"] = settings.Image,
                ["staticConfigurationHash"] = settings.LastAppliedStaticHash
            }, cancellationToken);
        return await SummarizeAsync(server, settings, cancellationToken);
    }

    private async Task StartCoreAsync(
        ServerExecutionTarget target,
        ServerProxySettings settings,
        bool forceRecreate,
        CancellationToken cancellationToken)
    {
        settings.PinImage(ComponentVersions.TraefikImage, timeProvider.GetUtcNow());
        IServerFileChannel channel = fileChannels.For(target);
        ProxyRuntimeBundle bundle = Render(target, channel, settings, settings.Image);

        foreach (ProxyRuntimeDirectory directory in bundle.Directories)
            await channel.EnsureDirectoryAsync(target, directory.RelativePath, directory.Mode, cancellationToken);
        foreach (ProxyRuntimeFile file in bundle.Files)
        {
            var write = new ServerFileWrite(file.RelativePath, file.Contents, file.Mode, file.CreateOnly);
            if (file.CreateOnly)
                await channel.WriteAsync(target, write, cancellationToken);
            else
                await WriteVerifiedAsync(target, channel, write, cancellationToken);
        }

        var network = ProxyNaming.ServerNetwork(target.ServerId);
        await runtime.EnsureNetworkAsync(target, network, cancellationToken);
        await runtime.UpAsync(target, channel.ResolveHostPath(target, bundle.ComposeRelativePath), forceRecreate,
            cancellationToken);

        ProxyContainerStatus status = await runtime.InspectAsync(target, cancellationToken);
        if (status.State != ProxyContainerState.Running)
            throw new DomainException(
                $"The proxy container is not running after start (state: {StateName(status.State)}).");
        settings.MarkStarted(bundle.Hash, timeProvider.GetUtcNow());
    }

    private static async Task WriteVerifiedAsync(
        ServerExecutionTarget target,
        IServerFileChannel channel,
        ServerFileWrite file,
        CancellationToken cancellationToken)
    {
        await channel.WriteAsync(target, file, cancellationToken);
        var written = await channel.ReadTextAsync(target, file.RelativePath, cancellationToken);
        if (!string.Equals(written, file.Contents, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Verification of '{file.RelativePath}' failed: the file on the server does not match what was written.");
    }

    private ProxyRuntimeBundle Render(
        ServerExecutionTarget target,
        IServerFileChannel channel,
        ServerProxySettings settings,
        string image)
    {
        if (settings.ProxyType != renderer.ProxyType)
            throw new DomainException($"Proxy type '{settings.ProxyType}' has no runtime renderer.");
        return renderer.Render(new ProxyRuntimeRenderRequest(
            target.ServerId,
            image,
            settings.HttpPort,
            settings.HttpsPort,
            ProxyNaming.ServerNetwork(target.ServerId),
            new ProxyHostPaths(
                channel.ResolveHostPath(target, ProxyLayout.StaticConfigurationFile),
                channel.ResolveHostPath(target, ProxyLayout.DynamicDirectory),
                channel.ResolveHostPath(target, ProxyLayout.AcmeDirectory))));
    }

    private async Task<ServerProxySummary> SummarizeAsync(
        Server server,
        ServerProxySettings settings,
        CancellationToken cancellationToken)
    {
        var imageOutdated = settings.RoutingEnabled &&
                            !string.Equals(settings.Image, ComponentVersions.TraefikImage, StringComparison.Ordinal);
        if (!settings.RoutingEnabled)
            return Summary(settings, "not-managed", null, true, false, imageOutdated, null);

        var target = ServerExecutionTarget.From(server);
        ProxyContainerStatus status;
        string? statusError = null;
        try
        {
            status = await runtime.InspectAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = new ProxyContainerStatus(ProxyContainerState.Unknown, null, null, []);
            statusError = redactor.Redact(ex.Message);
        }

        var restartRequired = false;
        if (status.State == ProxyContainerState.Running)
            try
            {
                ProxyRuntimeBundle current = Render(target, fileChannels.For(target), settings,
                    ComponentVersions.TraefikImage);
                restartRequired = settings.RequiresRestart(current.Hash);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                statusError ??= redactor.Redact(ex.Message);
            }

        return Summary(settings, StateName(status.State), status.Health, status.IsHealthy, restartRequired,
            imageOutdated, statusError);
    }

    private ServerProxySummary Summary(
        ServerProxySettings settings,
        string containerState,
        string? health,
        bool healthy,
        bool restartRequired,
        bool imageOutdated,
        string? statusError)
    {
        return new ServerProxySummary(
            settings.ServerId.Value,
            settings.ProxyType.ToString(),
            settings.Image,
            ComponentVersions.TraefikImage,
            imageOutdated,
            settings.HttpPort,
            settings.HttpsPort,
            settings.DesiredState.ToString(),
            containerState,
            health,
            healthy,
            restartRequired,
            settings.LastAppliedStaticHash,
            settings.LastAppliedDynamicHash,
            settings.LastStartedAt,
            settings.LastStoppedAt,
            settings.LastDynamicAppliedAt,
            settings.LastError is null ? null : redactor.Redact(settings.LastError),
            statusError);
    }

    private async Task<ProxyContainerStatus> InspectSafeAsync(
        ServerExecutionTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runtime.InspectAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProxyContainerStatus(ProxyContainerState.Unknown, null, null, []);
        }
    }

    private async Task<DistributedLockHandle> AcquireAsync(
        ServerId serverId,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        return await locks.TryAcquireAsync(LockKey(serverId), LockLease, wait, cancellationToken)
               ?? throw new DomainException("A proxy operation is already running for this server.");
    }

    private ServerProxySettings? FindSettings(ServerId serverId)
    {
        return _pendingSettings.TryGetValue(serverId, out ServerProxySettings? pending)
            ? pending
            : dbContext.ServerProxySettings.SingleOrDefault(settings => settings.Id == serverId);
    }

    private async Task<ServerProxySettings> GetOrCreateSettingsAsync(
        ServerId serverId,
        CancellationToken cancellationToken)
    {
        ServerProxySettings? existing = FindSettings(serverId);
        if (existing is not null)
            return existing;

        ServerProxySettings created = DefaultSettings(serverId);
        await dbContext.ServerProxySettingsRepository.AddAsync(created, cancellationToken);
        _pendingSettings[serverId] = created;
        return created;
    }

    private ServerProxySettings DefaultSettings(ServerId serverId)
    {
        return ServerProxySettings.CreateDefault(serverId, ComponentVersions.TraefikImage,
            timeProvider.GetUtcNow());
    }

    private Server LoadServer(ServerId serverId)
    {
        return dbContext.Servers.SingleOrDefault(item => item.Id == serverId)
               ?? throw new InvalidOperationException("Server was not found.");
    }

    private void RequireServer(UserId actorUserId, TeamId teamId, ServerId serverId, string permission)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), permission);
    }

    private static ServerProxyType? ParseProxyType(string? value)
    {
        if (value is null)
            return null;
        if (!int.TryParse(value, out _) &&
            Enum.TryParse(value.Trim(), true, out ServerProxyType parsed) &&
            Enum.IsDefined(parsed))
            return parsed;
        throw new DomainException("Proxy type must be 'Traefik' or 'None'.");
    }

    private static AuditTarget ServerTarget(ServerId serverId)
    {
        return new AuditTarget("server", serverId.Value.ToString("D"));
    }

    private static string StateName(ProxyContainerState state)
    {
        return state.ToString().ToLowerInvariant();
    }

    private enum ProxyAction
    {
        Start,
        Stop,
        Restart
    }
}
