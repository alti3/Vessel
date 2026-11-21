using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Realtime;
using Vessel.Application.Ssh;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Certificates;
using Vessel.Domain.Servers;

namespace Vessel.Application.Monitoring;

public sealed class ServerHealthPollingService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorizationService,
    IContainerRuntimeClient runtimeClient,
    IProxyStatusProbe proxyStatus,
    IRealtimeNotifier realtime,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    INotificationEventPublisher notifications)
{
    public const string HostRuntimeReachableReason = "Control-plane host container runtime responded.";
    public const string HostRuntimeUnreachableReason = "Control-plane host container runtime did not respond.";
    public const string SshReachableReason = "Remote container runtime responded over SSH.";
    public const string SshUnreachableReason =
        "The remote container runtime did not respond over SSH. Check the server's SSH key, host key and runtime, then validate the server.";
    public const string UnsafeAddressReason = "Server address or user contains characters that are not allowed for SSH.";

    public async Task<IReadOnlyList<ServerHealthPollingResult>> PollAllAsync(
        CancellationToken cancellationToken = default)
    {
        List<ServerHealthPollingResult> results = [];
        foreach (Server server in dbContext.Servers.Where(server => server.Status != ServerStatus.Disabled).ToArray())
            results.Add(await PollAsync(server.Id, cancellationToken));

        return results;
    }

    public async Task<ServerHealthPollingResult> PollAsync(
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        return await PollCoreAsync(serverId, null, cancellationToken);
    }

    public async Task<ServerHealthPollingResult> PollAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        if (!authorizationService.IsAuthorized(actorUserId, teamId, ResourceRef.Server(serverId),
                VesselPermissions.ServersWrite))
            throw new UnauthorizedAccessException("The current user is not authorized to poll this server.");

        Server server = dbContext.Servers.SingleOrDefault(candidate => candidate.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");
        if (server.TeamId != teamId)
            throw new UnauthorizedAccessException("The current user is not authorized to poll this server.");
        HostLocalExecutionGuard.RequireInstanceAdmin(authorizationService, actorUserId, server);

        return await PollCoreAsync(serverId, actorUserId, cancellationToken);
    }

    private async Task<ServerHealthPollingResult> PollCoreAsync(
        ServerId serverId,
        UserId? actorUserId,
        CancellationToken cancellationToken)
    {
        Server server = dbContext.Servers.SingleOrDefault(server => server.Id == serverId)
                        ?? throw new InvalidOperationException("Server was not found.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        ProbeOutcome probe = await ProbeAsync(server, actorUserId, cancellationToken);
        int runningContainers = probe.RunningContainers;
        bool runtimeReachable = probe.RuntimeReachable;
        ServerStatus nextStatus = probe.Status;

        // Real container state: a missing, stopped, or unhealthy proxy container is reported as not healthy.
        bool proxyHealthy = runtimeReachable && await proxyStatus.IsHealthyAsync(server, cancellationToken);

        bool certificatesHealthy = !dbContext.Certificates
            .Join(
                dbContext.Applications.Where(application => application.ServerId == server.Id),
                certificate => certificate.ApplicationId,
                application => application.Id,
                (certificate, _) => certificate)
            .Any(certificate => certificate.Status != CertificateStatus.Issued);

        var failuresBefore = server.ConsecutiveFailedHealthChecks;
        // Unknown outcomes (for example unattended host-local polling without authorization) do not count either way.
        if (nextStatus is ServerStatus.Reachable or ServerStatus.Unreachable)
            server.RecordHealthCheck(nextStatus == ServerStatus.Reachable);
        server.ChangeStatus(nextStatus, now);
        ServerStatusSnapshot snapshot = ServerStatusSnapshot.Create(
            server.Id,
            nextStatus,
            cpuLoadPercent: null,
            memoryUsedBytes: null,
            diskUsedBytes: null,
            runningContainers,
            proxyHealthy,
            certificatesHealthy,
            now);

        await dbContext.ServerStatusSnapshotRepository.AddAsync(snapshot, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(
            server.TeamId,
            actorUserId,
            AuditActions.ServerHealthPolled,
            new AuditTarget("server", server.Id.Value.ToString("D")),
            null,
            new Dictionary<string, object?>
            {
                ["status"] = nextStatus.ToString(),
                ["runningContainers"] = runningContainers,
                ["runtimeReachable"] = runtimeReachable,
                ["connectionType"] = server.ConnectionType.ToString(),
                ["reason"] = probe.Reason
            },
            cancellationToken);
        await realtime.PublishAsync(
            new RealtimeGroup(RealtimeGroupKind.Server, server.Id.Value.ToString("D")),
            new RealtimeMessage(
                "server.health",
                new ServerHealthSnapshotModel(
                    server.Id.Value,
                    nextStatus,
                    null,
                    null,
                    null,
                    runningContainers,
                    proxyHealthy,
                    certificatesHealthy,
                    now)),
            cancellationToken);

        // Unreachable is announced once the server failed Server.UnreachableAlertThreshold checks in a row (upstream
        // unreachable_count >= 2), so one dropped poll is not an outage; "reachable again" only follows an announced
        // outage (the catalog releases the incident). Never on every poll, so a down server raises one alert.
        if (nextStatus == ServerStatus.Unreachable && failuresBefore + 1 == Server.UnreachableAlertThreshold)
            await notifications.PublishAsync(server.TeamId,
                OperationalNotifications.ServerReachabilityChanged(server.Id.Value, server.Name.Value, false),
                CancellationToken.None);
        else if (nextStatus == ServerStatus.Reachable && failuresBefore >= Server.UnreachableAlertThreshold)
            await notifications.PublishAsync(server.TeamId,
                OperationalNotifications.ServerReachabilityChanged(server.Id.Value, server.Name.Value, true),
                CancellationToken.None);

        return new ServerHealthPollingResult(server.Id.Value, nextStatus, runningContainers, runtimeReachable,
            probe.Reason);
    }

    /// <summary>
    /// Probes the server over its own connection. Host-local servers query the control-plane runtime (only when
    /// host-local execution is authorized); SSH servers are probed over SSH and are never answered by the local
    /// runtime.
    /// </summary>
    private async Task<ProbeOutcome> ProbeAsync(Server server, UserId? actorUserId, CancellationToken cancellationToken)
    {
        if (server.IsHostLocal)
        {
            if (!HostLocalExecutionGuard.IsAuthorized(authorizationService, server, actorUserId))
                return new ProbeOutcome(ServerStatus.Unknown, 0, false,
                    HostLocalExecutionGuard.UnattendedNotAuthorizedMessage);

            return await ProbeHostRuntimeAsync(server, cancellationToken);
        }

        if (server.ConnectionType == ServerConnectionType.Ssh)
            return await ProbeSshAsync(server, cancellationToken);

        return new ProbeOutcome(ServerStatus.Unknown, 0, false,
            $"Connection type '{server.ConnectionType}' cannot be probed yet.");
    }

    private async Task<ProbeOutcome> ProbeHostRuntimeAsync(Server server, CancellationToken cancellationToken)
    {
        try
        {
            ContainerRuntimeTarget target = RuntimeTarget(server);
            await runtimeClient.GetInfoAsync(target, cancellationToken);
            IReadOnlyList<ContainerSummary> containers =
                await runtimeClient.ListContainersAsync(target, all: false, cancellationToken);
            int running = containers.Count(container =>
                string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase));
            return new ProbeOutcome(ServerStatus.Reachable, running, true, HostRuntimeReachableReason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProbeOutcome(ServerStatus.Unreachable, 0, false, HostRuntimeUnreachableReason);
        }
    }

    /// <summary>
    ///     Probes an SSH server's own runtime over SSH (the runtime CLI runs on the remote host; the control-plane runtime
    ///     is never consulted). Host and user are validated before any connection is attempted.
    /// </summary>
    private async Task<ProbeOutcome> ProbeSshAsync(Server server, CancellationToken cancellationToken)
    {
        string user = server.Address.User ?? "root";
        if (!SshTargetValidator.IsSafeHost(server.Address.Host) || !SshTargetValidator.IsSafeUser(user))
            return new ProbeOutcome(ServerStatus.Unreachable, 0, false, UnsafeAddressReason);

        try
        {
            ContainerRuntimeTarget target = ContainerRuntimeTarget.For(server);
            await runtimeClient.GetInfoAsync(target, cancellationToken);
            IReadOnlyList<ContainerSummary> containers =
                await runtimeClient.ListContainersAsync(target, all: false, cancellationToken);
            int running = containers.Count(container =>
                string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase));
            return new ProbeOutcome(ServerStatus.Reachable, running, true, SshReachableReason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProbeOutcome(ServerStatus.Unreachable, 0, false, SshUnreachableReason);
        }
    }

    private sealed record ProbeOutcome(ServerStatus Status, int RunningContainers, bool RuntimeReachable, string Reason);

    private static ContainerRuntimeTarget RuntimeTarget(Server server)
    {
        var provider = server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker;

        return new ContainerRuntimeTarget(provider);
    }
}
