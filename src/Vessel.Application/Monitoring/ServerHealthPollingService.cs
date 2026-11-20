using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Proxy;
using Vessel.Application.Realtime;
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
        int runningContainers = 0;
        bool runtimeReachable = false;
        ServerStatus nextStatus;

        try
        {
            ContainerRuntimeTarget target = RuntimeTarget(server);
            await runtimeClient.GetInfoAsync(target, cancellationToken);
            IReadOnlyList<ContainerSummary> containers =
                await runtimeClient.ListContainersAsync(target, all: false, cancellationToken);
            runningContainers = containers.Count(container =>
                string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase));
            runtimeReachable = true;
            nextStatus = ServerStatus.Reachable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            nextStatus = ServerStatus.Unreachable;
        }

        // Real container state: a missing, stopped, or unhealthy proxy container is reported as not healthy.
        bool proxyHealthy = runtimeReachable && await proxyStatus.IsHealthyAsync(server, cancellationToken);

        bool certificatesHealthy = !dbContext.Certificates
            .Join(
                dbContext.Applications.Where(application => application.ServerId == server.Id),
                certificate => certificate.ApplicationId,
                application => application.Id,
                (certificate, _) => certificate)
            .Any(certificate => certificate.Status != CertificateStatus.Issued);

        ServerStatus previousStatus = server.Status;
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
                ["runtimeReachable"] = runtimeReachable
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

        // Notify on reachability transitions only, never on every poll, so a down server raises one alert.
        if (IsReachabilityTransition(previousStatus, nextStatus))
            await notifications.PublishAsync(server.TeamId,
                OperationalNotifications.ServerReachabilityChanged(server.Id.Value, server.Name.Value,
                    nextStatus == ServerStatus.Reachable),
                CancellationToken.None);

        return new ServerHealthPollingResult(server.Id.Value, nextStatus, runningContainers, runtimeReachable);
    }

    private static bool IsReachabilityTransition(ServerStatus previous, ServerStatus next)
    {
        return next switch
        {
            // Became unreachable from any monitored state (a brand-new server failing its first poll included).
            ServerStatus.Unreachable => previous is not (ServerStatus.Unreachable or ServerStatus.Disabled),
            // Recovered: only announce when we previously announced it was down.
            ServerStatus.Reachable => previous == ServerStatus.Unreachable,
            _ => false
        };
    }

    private static ContainerRuntimeTarget RuntimeTarget(Server server)
    {
        var provider = server.Runtime == ContainerRuntimeKind.Podman
            ? ContainerRuntimeProvider.Podman
            : ContainerRuntimeProvider.Docker;

        return new ContainerRuntimeTarget(provider);
    }
}
