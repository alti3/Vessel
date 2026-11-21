using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Docker;
using Vessel.Application.Jobs;
using Vessel.Application.Monitoring;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Resources.Status;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Deployments;
using Vessel.Domain.Monitoring;
using Vessel.Domain.Servers;

namespace Vessel.Application.Servers.DockerCleanup;

public sealed record DockerCleanupPolicyDto(
    bool ScheduleEnabled,
    string CronExpression,
    int ThresholdPercent,
    bool ForceScheduledCleanup,
    bool DeleteUnusedVolumes,
    bool DeleteUnusedNetworks,
    DateTimeOffset? LastCleanupAt);

public sealed record UpdateDockerCleanupPolicyRequest(
    bool? ScheduleEnabled = null,
    string? CronExpression = null,
    int? ThresholdPercent = null,
    bool? ForceScheduledCleanup = null,
    bool? DeleteUnusedVolumes = null,
    bool? DeleteUnusedNetworks = null);

public sealed record DockerCleanupExecutionDto(
    Guid Id,
    Guid ServerId,
    string Trigger,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long ReclaimedBytes,
    int RemovedContainers,
    int RemovedImages,
    bool BuildCachePruned,
    string? Message,
    string Log);

public sealed record ServerImageDto(
    string Id,
    IReadOnlyList<string> Tags,
    long SizeBytes,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<string> UsedByContainers,
    bool Reclaimable,
    string? ReclaimReason);

public sealed record ServerImagesPageDto(
    int Page,
    int PageSize,
    int Total,
    long TotalBytes,
    long ReclaimableBytes,
    IReadOnlyList<ServerImageDto> Items);

/// <param name="Reference">Image tag or id to delete.</param>
/// <param name="ConfirmReference">Must repeat <paramref name="Reference" /> exactly (typed confirmation).</param>
public sealed record DeleteServerImageRequest(string Reference, string ConfirmReference);

/// <summary>
///     Docker cleanup (SRV-07): per-server policy, scheduled / disk-threshold / manual runs with history, and the
///     server Images page. Runs take a per-server lock (a manual run waits for a running one; scheduled and threshold
///     runs skip), scheduled and threshold runs skip while a deployment is active on the server, and every step is
///     idempotent. Volumes are never pruned unless the policy opts in, and then only anonymous volumes.
/// </summary>
public sealed class DockerCleanupService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IContainerRuntimeClient runtime,
    IDistributedLockManager locks,
    IBackgroundJobDispatcher jobs,
    IRecurringJobScheduler recurringJobs,
    INotificationEventPublisher notifications,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan LockLease = TimeSpan.FromMinutes(11);
    public static readonly TimeSpan ManualWait = TimeSpan.FromMinutes(10);
    public const int MaxPageSize = 200;

    public static string LockKey(ServerId serverId)
    {
        return $"servers:docker-cleanup:{serverId.Value:D}";
    }

    public static string RecurringJobId(ServerId serverId)
    {
        return $"servers.docker-cleanup.{serverId.Value:D}";
    }

    public DockerCleanupPolicyDto GetPolicy(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        ServerMonitoringSettings settings = Settings(serverId) ??
                                            ServerMonitoringSettings.CreateDefault(serverId, timeProvider.GetUtcNow());
        return ToDto(settings);
    }

    public async Task<DockerCleanupPolicyDto> UpdatePolicyAsync(UserId actorUserId, TeamId teamId, ServerId serverId,
        UpdateDockerCleanupPolicyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        ServerMonitoringSettings settings = await GetOrCreateSettingsAsync(serverId, cancellationToken);
        DockerCleanupPolicy current = settings.DockerCleanup;
        var policy = DockerCleanupPolicy.Create(
            request.ScheduleEnabled ?? current.ScheduleEnabled,
            request.CronExpression ?? current.CronExpression,
            request.ThresholdPercent ?? current.ThresholdPercent,
            request.ForceScheduledCleanup ?? current.ForceScheduledCleanup,
            request.DeleteUnusedVolumes ?? current.DeleteUnusedVolumes,
            request.DeleteUnusedNetworks ?? current.DeleteUnusedNetworks);
        settings.ConfigureDockerCleanup(policy, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        SyncRecurringJob(serverId, policy);
        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.DockerCleanupPolicyUpdated,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["scheduleEnabled"] = policy.ScheduleEnabled,
                ["cron"] = policy.CronExpression,
                ["thresholdPercent"] = policy.ThresholdPercent,
                ["forceScheduledCleanup"] = policy.ForceScheduledCleanup,
                ["deleteUnusedVolumes"] = policy.DeleteUnusedVolumes,
                ["deleteUnusedNetworks"] = policy.DeleteUnusedNetworks
            }, cancellationToken);
        return ToDto(settings);
    }

    /// <summary>Queues a manual cleanup (deploy-level access to the server, like upstream's deploy ability).</summary>
    public async Task<string> RequestManualRunAsync(UserId actorUserId, TeamId teamId, ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = dbContext.Servers.Single(item => item.Id == serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        if (server.Status == ServerStatus.Disabled) throw new DomainException("The server is disabled.");
        var id = serverId.Value;
        var actor = actorUserId.Value;
        var jobId = jobs.Enqueue<RunDockerCleanupJob>(job => job.RunManualAsync(id, actor, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.DockerCleanupRequested,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["jobId"] = jobId }, cancellationToken);
        return jobId;
    }

    public IReadOnlyList<DockerCleanupExecutionDto> ListExecutions(UserId actorUserId, TeamId teamId,
        ServerId serverId, int take = 50)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        return dbContext.DockerCleanupExecutions.Where(execution => execution.ServerId == serverId)
            .OrderByDescending(execution => execution.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .AsEnumerable()
            .Select(ToDto)
            .ToArray();
    }

    /// <summary>Runs one cleanup. Unattended triggers have no actor; a manual run carries the requesting user.</summary>
    public async Task<DockerCleanupExecutionDto?> RunAsync(ServerId serverId, DockerCleanupTrigger trigger,
        UserId? requestedBy, CancellationToken cancellationToken = default)
    {
        Server? server = dbContext.Servers.SingleOrDefault(item => item.Id == serverId);
        if (server is null || server.Status == ServerStatus.Disabled) return null;
        if (!HostLocalExecutionGuard.IsAuthorized(authorization, server, requestedBy)) return null;

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(LockKey(serverId), LockLease,
            trigger == DockerCleanupTrigger.Manual ? ManualWait : TimeSpan.Zero, cancellationToken);
        if (handle is null) return null; // One cleanup per server at a time; this occurrence is covered by it.

        ServerMonitoringSettings settings = await GetOrCreateSettingsAsync(serverId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DockerCleanupExecution execution = DockerCleanupExecution.Start(serverId, trigger, requestedBy, now);
        await dbContext.DockerCleanupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var skip = SkipReason(server, settings, trigger);
        if (skip is not null)
        {
            execution.Skip(skip, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToDto(execution);
        }

        var deploymentActive = DeploymentActive(serverId);
        var failed = false;
        try
        {
            failed = await ExecuteAsync(server, settings.DockerCleanup, execution, deploymentActive,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed = true;
            execution.AppendLog("Cleanup aborted: the server's container inventory could not be read.");
        }

        if (failed) execution.Fail("One or more cleanup steps failed. Review the execution log.", timeProvider.GetUtcNow());
        else execution.Succeed(timeProvider.GetUtcNow());
        settings.RecordDockerCleanup(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(CancellationToken.None);

        await notifications.PublishAsync(server.TeamId,
            MonitoringNotifications.DockerCleanupFinished(serverId.Value, server.Name.Value, execution.Id.Value,
                !failed, execution.ReclaimedBytes), CancellationToken.None);
        await auditWriter.RecordAsync(server.TeamId, requestedBy, MonitoringAuditActions.DockerCleanupFinished,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["executionId"] = execution.Id.Value,
                ["trigger"] = trigger.ToString(),
                ["status"] = execution.Status.ToString(),
                ["reclaimedBytes"] = execution.ReclaimedBytes,
                ["removedContainers"] = execution.RemovedContainers,
                ["removedImages"] = execution.RemovedImages
            }, CancellationToken.None);
        return ToDto(execution);
    }

    /// <summary>Marks runs of lost workers failed and keeps each server's recurring job in line with its policy.</summary>
    public async Task<int> SyncSchedulesAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - DockerCleanupExecution.InterruptedAfter;
        DockerCleanupExecution[] interrupted = dbContext.DockerCleanupExecutions
            .Where(execution => execution.Status == DockerCleanupStatus.Running && execution.StartedAt < cutoff)
            .ToArray();
        foreach (DockerCleanupExecution execution in interrupted)
            execution.Fail("The cleanup was interrupted (the worker stopped before it finished).", now);
        if (interrupted.Length > 0) await dbContext.SaveChangesAsync(cancellationToken);

        var settings = dbContext.ServerMonitoringSettings.AsEnumerable().ToDictionary(item => item.Id);
        foreach (Server server in dbContext.Servers.ToArray())
        {
            DockerCleanupPolicy policy = settings.TryGetValue(server.Id, out ServerMonitoringSettings? setting)
                ? setting.DockerCleanup
                : DockerCleanupPolicy.Default;
            SyncRecurringJob(server.Id, server.Status == ServerStatus.Disabled ? policy with { ScheduleEnabled = false } : policy);
        }

        return interrupted.Length;
    }

    public async Task<ServerImagesPageDto> ListImagesAsync(UserId actorUserId, TeamId teamId, ServerId serverId,
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        Server server = dbContext.Servers.Single(item => item.Id == serverId);
        // Listing images probes the runtime: on a host-local server only an instance admin may do that.
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        ContainerRuntimeTarget target = ContainerRuntimeTarget.For(server);
        IReadOnlyList<ImageDetails> images = await runtime.ListImageDetailsAsync(target, cancellationToken);
        IReadOnlyList<ContainerStateReading> containers = await runtime.ListContainerStatesAsync(target, cancellationToken);
        DockerCleanupPlan plan = DockerCleanupPlanner.Plan(containers, images, Inventory(), timeProvider.GetUtcNow());
        var reclaim = plan.ImagesToRemove.GroupBy(item => item.Reference)
            .ToDictionary(group => group.Key, group => group.First().Reason, StringComparer.Ordinal);

        ServerImageDto[] all = images.Select(image =>
            {
                var users = containers.Where(container => container.ImageId == image.Id ||
                                                          image.RepoTags.Contains(container.Image))
                    .Select(container => container.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var reason = image.RepoTags.Select(tag => reclaim.GetValueOrDefault(tag)).FirstOrDefault(r => r is not null);
                var reclaimable = image.RepoTags.Count == 0 ? users.Length == 0
                    : image.RepoTags.All(reclaim.ContainsKey);
                return new ServerImageDto(image.Id, image.RepoTags, image.SizeBytes, image.CreatedAt, users,
                    reclaimable, reason ?? (image.RepoTags.Count == 0 && users.Length == 0 ? "dangling image" : null));
            })
            .OrderByDescending(image => image.SizeBytes)
            .ThenBy(image => image.Id, StringComparer.Ordinal)
            .ToArray();
        return new ServerImagesPageDto(page, pageSize, all.Length, all.Sum(image => image.SizeBytes),
            all.Where(image => image.Reclaimable).Sum(image => image.SizeBytes),
            all.Skip((page - 1) * pageSize).Take(pageSize).ToArray());
    }

    /// <summary>Deletes one listed, unused image after a typed confirmation (never forced).</summary>
    public async Task DeleteImageAsync(UserId actorUserId, TeamId teamId, ServerId serverId,
        DeleteServerImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = dbContext.Servers.Single(item => item.Id == serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        var reference = request.Reference?.Trim() ?? string.Empty;
        if (reference.Length == 0 || !string.Equals(reference, request.ConfirmReference?.Trim(), StringComparison.Ordinal))
            throw new DomainException("Type the image reference exactly to confirm the deletion.");

        ContainerRuntimeTarget target = ContainerRuntimeTarget.For(server);
        IReadOnlyList<ImageDetails> images = await runtime.ListImageDetailsAsync(target, cancellationToken);
        ImageDetails image = images.FirstOrDefault(item => item.Id == reference || item.RepoTags.Contains(reference))
                             ?? throw new KeyNotFoundException("The image is not present on this server.");
        IReadOnlyList<ContainerStateReading> containers = await runtime.ListContainerStatesAsync(target, cancellationToken);
        if (containers.Any(container => container.ImageId == image.Id || container.Image == reference))
            throw new InvalidOperationException("The image is used by a container and cannot be deleted.");
        if (!await runtime.RemoveImageAsync(target, reference, cancellationToken))
            throw new InvalidOperationException("The container runtime refused to delete the image.");
        await auditWriter.RecordAsync(teamId, actorUserId, MonitoringAuditActions.ImageDeleted,
            new AuditTarget("server", serverId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["image"] = reference, ["sizeBytes"] = image.SizeBytes },
            cancellationToken);
    }

    private async Task<bool> ExecuteAsync(Server server, DockerCleanupPolicy policy,
        DockerCleanupExecution execution, bool deploymentActive, CancellationToken cancellationToken)
    {
        ContainerRuntimeTarget target = ContainerRuntimeTarget.For(server);
        var failed = false;
        IReadOnlyList<ContainerStateReading> containers = await runtime.ListContainerStatesAsync(target, cancellationToken);
        IReadOnlyList<ImageDetails> images = await runtime.ListImageDetailsAsync(target, cancellationToken);
        DockerCleanupPlan plan = DockerCleanupPlanner.Plan(containers, images, Inventory(), timeProvider.GetUtcNow());

        if (plan.ContainersToRemove.Count > 0)
        {
            var removed = await runtime.RemoveContainersAsync(target,
                plan.ContainersToRemove.Select(container => container.Id).ToArray(), cancellationToken);
            execution.RecordRemoval(removed, 0, 0, false);
            execution.AppendLog($"Removed {removed} of {plan.ContainersToRemove.Count} stopped orphaned Vessel containers.");
        }
        else
        {
            execution.AppendLog("No stopped orphaned Vessel containers.");
        }

        int removedImages = 0;
        long reclaimed = 0;
        foreach (PlannedImageRemoval removal in plan.ImagesToRemove)
        {
            // Never forced: an image a container started using meanwhile stays.
            if (!await runtime.RemoveImageAsync(target, removal.Reference, cancellationToken)) continue;
            removedImages++;
            reclaimed += removal.ReclaimableBytes;
        }

        execution.RecordRemoval(0, removedImages, reclaimed, false);
        execution.AppendLog($"Removed {removedImages} of {plan.ImagesToRemove.Count} unused image references " +
                            $"({MonitoringNotifications.FormatBytes(reclaimed)}); rollback images were kept.");

        failed |= !await PruneStepAsync(target, RuntimePruneKind.DanglingImages, "Dangling images", execution,
            cancellationToken);
        if (deploymentActive)
            execution.AppendLog("Build cache kept: a deployment is active on this server.");
        else
            failed |= !await PruneStepAsync(target, RuntimePruneKind.BuildCache, "Build cache", execution,
                cancellationToken);
        if (policy.DeleteUnusedNetworks)
            failed |= !await PruneStepAsync(target, RuntimePruneKind.Networks, "Unused networks", execution,
                cancellationToken);
        if (policy.DeleteUnusedVolumes)
            failed |= !await PruneStepAsync(target, RuntimePruneKind.AnonymousVolumes, "Unused anonymous volumes",
                execution, cancellationToken);
        else
            execution.AppendLog("Volumes kept (volume cleanup is off).");
        return failed;
    }

    private static async Task<bool> PruneStepAsync(IContainerRuntimeClient runtime, ContainerRuntimeTarget target,
        RuntimePruneKind kind, string label, DockerCleanupExecution execution, CancellationToken cancellationToken)
    {
        RuntimePruneResult result;
        try
        {
            result = await runtime.PruneAsync(target, kind, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.AppendLog($"{label}: failed.");
            return false;
        }

        if (!result.Supported)
        {
            execution.AppendLog($"{label}: not supported by this runtime, skipped.");
            return true;
        }

        execution.RecordRemoval(0, 0, result.ReclaimedBytes, kind == RuntimePruneKind.BuildCache && result.Succeeded);
        execution.AppendLog(result.Succeeded
            ? $"{label}: pruned ({MonitoringNotifications.FormatBytes(result.ReclaimedBytes)})."
            : $"{label}: failed.");
        return result.Succeeded;
    }

    private Task<bool> PruneStepAsync(ContainerRuntimeTarget target, RuntimePruneKind kind, string label,
        DockerCleanupExecution execution, CancellationToken cancellationToken)
    {
        return PruneStepAsync(runtime, target, kind, label, execution, cancellationToken);
    }

    private string? SkipReason(Server server, ServerMonitoringSettings settings, DockerCleanupTrigger trigger)
    {
        if (trigger == DockerCleanupTrigger.Manual) return null;
        if (DeploymentActive(server.Id))
            return "Skipped: a deployment is active on this server; the next occurrence cleans up.";
        if (trigger == DockerCleanupTrigger.Scheduled && settings.DockerCleanup.ForceScheduledCleanup) return null;
        decimal? usage = dbContext.ServerMetricSamples
            .Where(sample => sample.ServerId == server.Id && sample.Resolution == MetricResolution.Raw)
            .OrderByDescending(sample => sample.BucketStart)
            .AsEnumerable()
            .Select(sample => sample.DiskPercent)
            .FirstOrDefault();
        if (usage is null) return null;
        return usage < settings.DockerCleanup.ThresholdPercent
            ? $"Skipped: disk usage is below the {settings.DockerCleanup.ThresholdPercent}% cleanup threshold."
            : null;
    }

    private bool DeploymentActive(ServerId serverId)
    {
        DateTimeOffset recent = timeProvider.GetUtcNow() - TimeSpan.FromHours(2);
        return dbContext.Deployments.Any(deployment => deployment.ServerId == serverId &&
                                                       (deployment.Status == DeploymentStatus.Queued ||
                                                        deployment.Status == DeploymentStatus.InProgress ||
                                                        deployment.Status == DeploymentStatus.CancelRequested) &&
                                                       deployment.UpdatedAt >= recent);
    }

    private DockerCleanupInventory Inventory()
    {
        var applications = dbContext.Applications.Select(application => application.Id).AsEnumerable()
            .Select(id => id.Value).ToHashSet();
        var databases = dbContext.DatabaseResources
            .Where(database => database.LifecycleState != DatabaseLifecycleState.Deleted)
            .Select(database => database.Id).AsEnumerable().Select(id => id.Value).ToHashSet();
        var services = dbContext.ServiceResources.Select(service => service.Id).AsEnumerable()
            .ToDictionary(id => RuntimeResourceLabels.ServiceProjectName(id.Value), id => id.Value,
                StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var application in dbContext.Applications.AsEnumerable())
        {
            var keep = application.DeploymentSettings.ImagesToKeep + 1;
            retained.UnionWith(dbContext.Deployments
                .Where(deployment => deployment.ApplicationId == application.Id && deployment.PreviewId == null &&
                                     deployment.Status == DeploymentStatus.Succeeded &&
                                     deployment.ArtifactReference != null)
                .OrderByDescending(deployment => deployment.FinishedAt)
                .Select(deployment => deployment.ArtifactReference!)
                .AsEnumerable()
                .Distinct(StringComparer.Ordinal)
                .Take(keep));
        }

        return new DockerCleanupInventory(applications, databases, services, retained);
    }

    private void SyncRecurringJob(ServerId serverId, DockerCleanupPolicy policy)
    {
        try
        {
            if (policy.ScheduleEnabled)
            {
                var id = serverId.Value;
                recurringJobs.AddOrUpdate<RunDockerCleanupJob>(RecurringJobId(serverId),
                    job => job.RunAsync(id, DockerCleanupTrigger.Scheduled, CancellationToken.None),
                    policy.CronExpression);
            }
            else
            {
                recurringJobs.RemoveIfExists(RecurringJobId(serverId));
            }
        }
        catch (BackgroundJobsUnavailableException)
        {
            // Without Hangfire storage there is no scheduler; the policy is still saved.
        }
    }

    private ServerMonitoringSettings? Settings(ServerId serverId)
    {
        return dbContext.ServerMonitoringSettings.SingleOrDefault(item => item.Id == serverId);
    }

    private async Task<ServerMonitoringSettings> GetOrCreateSettingsAsync(ServerId serverId,
        CancellationToken cancellationToken)
    {
        ServerMonitoringSettings? settings = Settings(serverId);
        if (settings is not null) return settings;
        settings = ServerMonitoringSettings.CreateDefault(serverId, timeProvider.GetUtcNow());
        await dbContext.ServerMonitoringSettingsRepository.AddAsync(settings, cancellationToken);
        return settings;
    }

    private static DockerCleanupPolicyDto ToDto(ServerMonitoringSettings settings)
    {
        DockerCleanupPolicy policy = settings.DockerCleanup;
        return new DockerCleanupPolicyDto(policy.ScheduleEnabled, policy.CronExpression, policy.ThresholdPercent,
            policy.ForceScheduledCleanup, policy.DeleteUnusedVolumes, policy.DeleteUnusedNetworks,
            settings.LastDockerCleanupAt);
    }

    public static DockerCleanupExecutionDto ToDto(DockerCleanupExecution execution)
    {
        return new DockerCleanupExecutionDto(execution.Id.Value, execution.ServerId.Value,
            execution.Trigger.ToString(), execution.Status.ToString(), execution.StartedAt, execution.FinishedAt,
            execution.ReclaimedBytes, execution.RemovedContainers, execution.RemovedImages,
            execution.BuildCachePruned, execution.Message, execution.Log);
    }
}

public sealed class RunDockerCleanupJob(DockerCleanupService cleanup)
{
    public Task RunAsync(Guid serverId, DockerCleanupTrigger trigger, CancellationToken cancellationToken = default)
    {
        return cleanup.RunAsync(new ServerId(serverId), trigger, null, cancellationToken);
    }

    public Task RunManualAsync(Guid serverId, Guid requestedBy, CancellationToken cancellationToken = default)
    {
        return cleanup.RunAsync(new ServerId(serverId), DockerCleanupTrigger.Manual, new UserId(requestedBy),
            cancellationToken);
    }
}

public sealed class DockerCleanupScheduleSyncJob(DockerCleanupService cleanup)
{
    public const string RecurringJobId = "servers.docker-cleanup.sync";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return cleanup.SyncSchedulesAsync(cancellationToken);
    }

    public static void Register(IRecurringJobScheduler scheduler)
    {
        scheduler.AddOrUpdate<DockerCleanupScheduleSyncJob>(RecurringJobId, job => job.RunAsync(CancellationToken.None),
            "15 * * * *");
    }
}
