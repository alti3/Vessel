using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Realtime;
using Vessel.Domain;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;
using Vessel.Domain.Services;
using ApplicationId = Vessel.Domain.ApplicationId;

namespace Vessel.Application.Resources.Status;

/// <summary>Result of reconciling one server's container snapshot.</summary>
public sealed record ResourceReconciliationResult(
    int ResourcesObserved,
    int StateChanges,
    int StopNotificationsScheduled,
    int RestartLimitsReached);

/// <summary>
///     Reconciles resource runtime status from a container snapshot of one server (SRV-04,
///     <c>ReconcileServerContainersCommand</c>): applications, databases and services are mapped by Vessel labels,
///     aggregated with upstream's priority rules, restart counts are tracked against the opt-in restart limit, and a
///     resource that stopped without a stop from Vessel gets a delayed re-check
///     (<see cref="NotifyResourceStoppedJob" />, 120 s) before anyone is notified. A resource without containers is
///     marked exited only when the snapshot is complete.
/// </summary>
public sealed class ResourceStatusReconciler(
    IVesselDbContext dbContext,
    IContainerRuntimeClient runtime,
    IBackgroundJobDispatcher jobs,
    INotificationEventPublisher notifications,
    IRealtimeNotifier realtime,
    TimeProvider timeProvider)
{
    public async Task<ResourceReconciliationResult> ReconcileAsync(Server server,
        IReadOnlyList<ContainerStateReading> containers, bool snapshotComplete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(containers);
        DateTimeOffset now = timeProvider.GetUtcNow();

        var resources = LoadResources(server.Id);
        if (resources.Count == 0) return new ResourceReconciliationResult(0, 0, 0, 0);

        var serviceProjects = resources.Where(resource => resource.Key.Kind == ResourceRuntimeKind.Service)
            .ToDictionary(resource => RuntimeResourceLabels.ServiceProjectName(resource.Key.Id),
                resource => resource.Key.Id, StringComparer.Ordinal);
        var byResource = containers
            .Select(container => (Key: RuntimeResourceLabels.Resolve(container.Labels, serviceProjects), container))
            .Where(item => item.Key.HasValue)
            .GroupBy(item => item.Key!.Value, item => item.container)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var resourceIds = resources.Select(resource => resource.Key.Id).ToArray();
        var statuses = dbContext.ResourceRuntimeStatuses
            .Where(status => resourceIds.Contains(status.ResourceId))
            .AsEnumerable()
            .ToDictionary(status => new RuntimeResourceKey(status.Kind, status.ResourceId));

        int observed = 0, changes = 0, scheduled = 0, limits = 0;
        foreach (RuntimeResource resource in resources)
        {
            ContainerStateReading[] own = byResource.TryGetValue(resource.Key, out var found) ? found : [];
            if (own.Length == 0 && !snapshotComplete) continue;

            if (!statuses.TryGetValue(resource.Key, out ResourceRuntimeStatus? status))
            {
                status = ResourceRuntimeStatus.Create(resource.Key.Kind, resource.Key.Id, server.TeamId, server.Id,
                    now);
                await dbContext.ResourceRuntimeStatusRepository.AddAsync(status, cancellationToken);
                statuses[resource.Key] = status;
            }

            var aggregated = ContainerStatusAggregator.Aggregate(
                own.Select(container => new ContainerObservation(container.State, container.Health,
                    container.RestartCount)).ToArray(),
                preserveRestarting: resource.Key.Kind != ResourceRuntimeKind.Service);
            var running = own.Count(container =>
                string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase));
            ResourceRuntimeObservation observation = status.Observe(aggregated, own.Length, running,
                own.Length == 0 ? 0 : own.Max(container => container.RestartCount), now);
            observed++;

            if (status.RestartLimitReached)
            {
                // Stop again while a container still runs: an earlier stop can fail or come too late (upstream).
                string[] active = own.Where(container => container.State is "running" or "restarting")
                    .Select(container => container.Id).ToArray();
                if (observation.RestartLimitReachedNow || active.Length > 0)
                    await StopAfterRestartLimitAsync(server, status, active, now, cancellationToken);
                if (observation.RestartLimitReachedNow)
                {
                    limits++;
                    await notifications.PublishAsync(server.TeamId,
                        MonitoringNotifications.RestartLimitReached(resource.Key.Kind, resource.Key.Id,
                            resource.Name, status.RestartCount, status.MaxRestartCount), CancellationToken.None);
                }
            }

            if (observation.StoppedUnexpectedly && !resource.StopExpectedByLifecycle)
            {
                var kind = resource.Key.Kind;
                var id = resource.Key.Id;
                var changedAt = status.StateChangedAt.UtcTicks;
                jobs.Schedule<NotifyResourceStoppedJob>(job => job.RunAsync(kind, id, changedAt,
                    CancellationToken.None), NotifyResourceStoppedJob.ConfirmationDelay);
                scheduled++;
            }

            if (observation.StateChanged)
            {
                changes++;
                await PublishStatusAsync(server, resource, status, cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new ResourceReconciliationResult(observed, changes, scheduled, limits);
    }

    private async Task StopAfterRestartLimitAsync(Server server, ResourceRuntimeStatus status,
        string[] containerIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        status.ExpectStop(now);
        if (containerIds.Length == 0) return;
        try
        {
            await runtime.StopContainersAsync(ContainerRuntimeTarget.For(server), containerIds, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Retried on the next reconciliation while a container still runs.
        }
    }

    private async Task PublishStatusAsync(Server server, RuntimeResource resource, ResourceRuntimeStatus status,
        CancellationToken cancellationToken)
    {
        var payload = ResourceRuntimeStatusService.ToDto(status);
        try
        {
            await realtime.PublishAsync(new RealtimeGroup(RealtimeGroupKind.Server, server.Id.Value.ToString("D")),
                new RealtimeMessage("resource.status", payload), cancellationToken);
            if (resource.Key.Kind == ResourceRuntimeKind.Application)
                await realtime.PublishAsync(
                    new RealtimeGroup(RealtimeGroupKind.Application, resource.Key.Id.ToString("D")),
                    new RealtimeMessage("resource.status", payload), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Realtime delivery is best effort.
        }
    }

    private List<RuntimeResource> LoadResources(ServerId serverId)
    {
        var resources = new List<RuntimeResource>();
        resources.AddRange(dbContext.Applications.Where(application => application.ServerId == serverId)
            .AsEnumerable()
            .Select(application => new RuntimeResource(
                new RuntimeResourceKey(ResourceRuntimeKind.Application, application.Id.Value),
                application.Name.Value, false)));
        resources.AddRange(dbContext.DatabaseResources
            .Where(database => database.ServerId == serverId && database.LifecycleState != DatabaseLifecycleState.Deleted)
            .AsEnumerable()
            .Select(database => new RuntimeResource(
                new RuntimeResourceKey(ResourceRuntimeKind.Database, database.Id.Value), database.Name.Value,
                // A stop, restart, delete or (re)provisioning Vessel runs is expected.
                database.LifecycleState is not DatabaseLifecycleState.Running)));
        resources.AddRange(dbContext.ServiceResources.Where(service => service.ServerId == serverId)
            .AsEnumerable()
            .Select(service => new RuntimeResource(
                new RuntimeResourceKey(ResourceRuntimeKind.Service, service.Id.Value), service.Name.Value,
                service.State is not DatabaseLifecycleState.Running)));
        return resources;
    }

    private sealed record RuntimeResource(RuntimeResourceKey Key, string Name, bool StopExpectedByLifecycle);
}

/// <summary>
///     Sends the "resource stopped" notification when a resource is still stopped
///     <see cref="ConfirmationDelay" /> after it stopped without a stop from Vessel (upstream
///     <c>NotifyResourceStoppedJob</c>). A resource that is up again, reached its restart limit, was stopped by Vessel,
///     changed state again, or is being deployed sends nothing.
/// </summary>
public sealed class NotifyResourceStoppedJob(
    IVesselDbContext dbContext,
    INotificationEventPublisher notifications,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan ConfirmationDelay = TimeSpan.FromSeconds(120);

    public async Task<bool> RunAsync(ResourceRuntimeKind kind, Guid resourceId, long stateChangedAtUtcTicks,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        ResourceRuntimeStatus? status = dbContext.ResourceRuntimeStatuses
            .SingleOrDefault(item => item.Kind == kind && item.ResourceId == resourceId);
        if (status is null || status.State != ResourceRuntimeState.Exited || status.RestartLimitReached ||
            status.IsStopExpected(now) || status.StateChangedAt.UtcTicks != stateChangedAtUtcTicks)
            return false;

        Server? server = dbContext.Servers.SingleOrDefault(item => item.Id == status.ServerId);
        if (server is null) return false;
        string? name;
        switch (kind)
        {
            case ResourceRuntimeKind.Application:
                var applicationId = new ApplicationId(resourceId);
                if (dbContext.Deployments.Any(deployment => deployment.ApplicationId == applicationId &&
                                                            (deployment.Status == DeploymentStatus.Queued ||
                                                             deployment.Status == DeploymentStatus.InProgress ||
                                                             deployment.Status == DeploymentStatus.CancelRequested)))
                    return false;
                name = dbContext.Applications.SingleOrDefault(application => application.Id == applicationId)
                    ?.Name.Value;
                break;
            case ResourceRuntimeKind.Database:
                var databaseId = new DatabaseResourceId(resourceId);
                DatabaseResource? database = dbContext.DatabaseResources.SingleOrDefault(item => item.Id == databaseId);
                if (database is null || database.LifecycleState != DatabaseLifecycleState.Running) return false;
                name = database.Name.Value;
                break;
            default:
                var serviceId = new ServiceResourceId(resourceId);
                ServiceResource? service = dbContext.ServiceResources.SingleOrDefault(item => item.Id == serviceId);
                if (service is null || service.State != DatabaseLifecycleState.Running) return false;
                name = service.Name.Value;
                break;
        }

        if (name is null) return false;
        await notifications.PublishAsync(status.TeamId,
            MonitoringNotifications.ResourceStoppedUnexpectedly(kind, resourceId, name, server.Name.Value),
            cancellationToken);
        return true;
    }
}
