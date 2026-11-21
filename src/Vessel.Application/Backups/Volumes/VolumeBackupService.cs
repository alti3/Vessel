using Vessel.Application.Authorization;
using Vessel.Application.Auditing;
using Vessel.Application.Jobs;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Servers;
using Vessel.Application.Servers.Files;
using Vessel.Application.Storage;
using Vessel.Application.Storage.Persistent;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Servers;
using Vessel.Domain.Storage;

namespace Vessel.Application.Backups.Volumes;

/// <summary>
///     Scheduled volume and directory backups (DATA-09, upstream <c>VolumeBackupJob</c>): one schedule per persistent
///     volume or directory mount; runs archive the target with tar+gzip in a helper container on the resource's server,
///     optionally with the mounting containers stopped (recorded first, restarted afterwards, compensated by the
///     recovery job with bounded attempts), store the archive locally and on team destinations, apply local and remote
///     retention, raise throttled notifications and missing-backup alerts, and restore archives as background jobs.
///     Backups and restores of the same target never overlap (lock <see cref="LockKey" />).
/// </summary>
public sealed class VolumeBackupService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    StorageOwnerAccess owners,
    IBackgroundJobDispatcher jobs,
    IRecurringJobScheduler recurringJobs,
    IDistributedLockManager locks,
    ISecretRedactor redactor,
    IVolumeArchiveRunner runner,
    IBackupStorageProvider storage,
    BackupDestinationConnector connector,
    IEnumerable<IServerFileChannel> channels,
    IAuditWriter auditWriter,
    INotificationEventPublisher notifications,
    TimeProvider timeProvider)
{
    public const string ConfirmationPhrase = "RESTORE";

    public const int MaxPageSize = 100;

    public const string MissingCheckLockKey = "volume-backups:missing-check";

    public const string RecoveryLockKey = "volume-backups:recovery";

    public static readonly TimeSpan RestoreTimeout = TimeSpan.FromHours(10);

    /// <summary>Executions older than this are not recovered automatically any more (they need attention).</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(7);

    /// <summary>Minimum spacing between two recovery attempts of one execution.</summary>
    public static readonly TimeSpan RecoveryRetryInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LockLeaseMargin = TimeSpan.FromMinutes(10);

    public static string RecurringJobId(ScheduledVolumeBackupId scheduleId)
    {
        return $"volume-backup:{scheduleId.Value:D}";
    }

    public static string LockKey(VolumeBackupTargetKind kind, Guid targetId)
    {
        return $"volume-backup:{kind}:{targetId:D}";
    }

    // ---- Schedules -------------------------------------------------------------------------------------------

    public IReadOnlyList<VolumeBackupScheduleSummary> ListSchedules(UserId actorUserId, TeamId teamId,
        StorageOwnerKind ownerKind, Guid ownerId)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, ownerKind, ownerId, false);
        return dbContext.ScheduledVolumeBackups
            .Where(schedule => schedule.OwnerKind == owner.Kind && schedule.OwnerId == owner.Id &&
                               schedule.TeamId == owner.TeamId)
            .AsEnumerable().OrderBy(schedule => schedule.CreatedAt).Select(ToSummary).ToArray();
    }

    public async Task<VolumeBackupScheduleSummary> CreateScheduleAsync(UserId actorUserId, TeamId teamId,
        StorageOwnerKind ownerKind, Guid ownerId, Guid storageId, CreateVolumeBackupScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, ownerKind, ownerId, true);
        RequireHostLocalAdmin(actorUserId, owner);
        (VolumeBackupTargetKind kind, _) = ResolveTargetForSchedule(owner, storageId);
        if (dbContext.ScheduledVolumeBackups.Any(schedule => schedule.TargetKind == kind &&
                                                             schedule.TargetId == storageId))
            throw new DomainException("This storage already has a backup schedule.");
        var schedule = ScheduledVolumeBackup.Create(owner.TeamId, owner.Kind, owner.Id, kind, storageId, Now());
        schedule.Configure(request.CronExpression, request.StopDuringBackup,
            ToRetention(request.LocalRetention, ScheduledVolumeBackup.DefaultRetentionCount),
            ToRetention(request.RemoteRetention, ScheduledVolumeBackup.DefaultRetentionCount),
            ResolveDestinations(owner.TeamId, request.DestinationIds), request.DisableLocalCopy,
            request.TimeoutSeconds ?? ScheduledVolumeBackup.DefaultTimeoutSeconds, request.MissingAlertDays, Now());
        if (!request.Enabled) schedule.Disable(Now());
        await dbContext.ScheduledVolumeBackupRepository.AddAsync(schedule, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        SyncRecurringJob(schedule);
        await AuditAsync(owner.TeamId, actorUserId, StorageAuditActions.VolumeBackupScheduleCreated, schedule,
            cancellationToken);
        return ToSummary(schedule);
    }

    public async Task<VolumeBackupScheduleSummary> UpdateScheduleAsync(UserId actorUserId, TeamId teamId,
        ScheduledVolumeBackupId scheduleId, UpdateVolumeBackupScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        (ScheduledVolumeBackup schedule, StorageOwnerContext owner) =
            await GetScheduleAsync(actorUserId, teamId, scheduleId, true, cancellationToken);
        RequireHostLocalAdmin(actorUserId, owner);
        schedule.Configure(
            request.CronExpression ?? schedule.CronExpression,
            request.StopDuringBackup ?? schedule.StopDuringBackup,
            request.LocalRetention is null ? schedule.LocalRetention : ToRetention(request.LocalRetention, 1),
            request.RemoteRetention is null ? schedule.RemoteRetention : ToRetention(request.RemoteRetention, 0),
            request.DestinationIds is null
                ? schedule.DestinationIds
                : ResolveDestinations(owner.TeamId, request.DestinationIds),
            request.DisableLocalCopy ?? schedule.DisableLocalCopy,
            request.TimeoutSeconds ?? schedule.TimeoutSeconds,
            request.MissingAlertDays ?? schedule.MissingAlertDays,
            Now());
        if (request.Enabled == true) schedule.Enable(Now());
        if (request.Enabled == false) schedule.Disable(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        SyncRecurringJob(schedule);
        await AuditAsync(owner.TeamId, actorUserId, StorageAuditActions.VolumeBackupScheduleUpdated, schedule,
            cancellationToken);
        return ToSummary(schedule);
    }

    /// <summary>
    ///     Deletes a schedule and its executions. By default the local and remote archives are deleted too (upstream
    ///     v4.3.22); with <paramref name="deleteArtifacts" /> false the files are kept but no longer tracked.
    /// </summary>
    public async Task DeleteScheduleAsync(UserId actorUserId, TeamId teamId, ScheduledVolumeBackupId scheduleId,
        bool deleteArtifacts, CancellationToken cancellationToken = default)
    {
        (ScheduledVolumeBackup schedule, StorageOwnerContext owner) =
            await GetScheduleAsync(actorUserId, teamId, scheduleId, true, cancellationToken);
        VolumeBackupExecution[] executions = dbContext.VolumeBackupExecutions
            .Where(execution => execution.ScheduleId == schedule.Id).ToArray();
        if (executions.Any(IsBusy))
            throw new VolumeBackupConflictException(
                "A backup or restore of this storage is running or needs recovery; delete the schedule after it finishes.");
        recurringJobs.RemoveIfExists(RecurringJobId(schedule.Id));
        var deleted = 0;
        if (deleteArtifacts)
            foreach (VolumeBackupExecution execution in executions)
                deleted += await DeleteCopiesAsync(execution, true, cancellationToken);
        foreach (VolumeBackupExecution execution in executions)
            dbContext.VolumeBackupExecutionRepository.Remove(execution);
        dbContext.ScheduledVolumeBackupRepository.Remove(schedule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.VolumeBackupScheduleDeleted,
            new AuditTarget("volume-backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["targetId"] = schedule.TargetId.ToString("D"),
                ["executions"] = executions.Length,
                ["deletedCopies"] = deleted
            }, cancellationToken);
    }

    // ---- Executions ------------------------------------------------------------------------------------------

    public async Task<PagedVolumeBackupExecutions> ListExecutionsAsync(UserId actorUserId, TeamId teamId,
        ScheduledVolumeBackupId scheduleId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        (ScheduledVolumeBackup schedule, _) =
            await GetScheduleAsync(actorUserId, teamId, scheduleId, false, cancellationToken);
        var sensitive = authorization.HasPermission(actorUserId, teamId, VesselPermissions.SecretsRead);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        IQueryable<VolumeBackupExecution> query =
            dbContext.VolumeBackupExecutions.Where(execution => execution.ScheduleId == schedule.Id);
        var total = query.Count();
        VolumeBackupExecutionSummary[] items = query.OrderByDescending(execution => execution.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).AsEnumerable()
            .Select(execution => ToSummary(execution, sensitive)).ToArray();
        return new PagedVolumeBackupExecutions(items, page, pageSize, total);
    }

    /// <summary>Queues a manual run of a schedule (upstream <c>…/backups/{id}/run</c>).</summary>
    public async Task<VolumeBackupExecutionSummary> RunNowAsync(UserId actorUserId, TeamId teamId,
        ScheduledVolumeBackupId scheduleId, CancellationToken cancellationToken = default)
    {
        (ScheduledVolumeBackup schedule, StorageOwnerContext owner) =
            await GetScheduleAsync(actorUserId, teamId, scheduleId, true, cancellationToken);
        RequireHostLocalAdmin(actorUserId, owner);
        var execution = VolumeBackupExecution.Queue(schedule.TeamId, schedule.Id, true, Now());
        await dbContext.VolumeBackupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = jobs.Enqueue<RunVolumeBackupExecutionJob>(job =>
            job.RunAsync(execution.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.VolumeBackupQueued,
            new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["jobId"] = jobId }, cancellationToken);
        return ToSummary(execution, true);
    }

    public async Task<VolumeBackupExecutionSummary?> RunScheduledAsync(ScheduledVolumeBackupId scheduleId,
        CancellationToken cancellationToken = default)
    {
        ScheduledVolumeBackup? schedule =
            await dbContext.ScheduledVolumeBackupRepository.GetByIdAsync(scheduleId, cancellationToken);
        if (schedule is null || !schedule.Enabled)
        {
            recurringJobs.RemoveIfExists(RecurringJobId(scheduleId));
            return null;
        }

        schedule.RecordRun(Now());
        var execution = VolumeBackupExecution.Queue(schedule.TeamId, schedule.Id, false, Now());
        await dbContext.VolumeBackupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await RunExecutionAsync(execution.Id, cancellationToken);
    }

    public async Task<VolumeBackupExecutionSummary> RunExecutionAsync(VolumeBackupExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        VolumeBackupExecution execution =
            await dbContext.VolumeBackupExecutionRepository.GetByIdAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException("Volume backup execution was not found.");
        // Retry-safe: a Hangfire retry of an execution that already ran does not archive again.
        if (execution.Status != VolumeBackupStatus.Queued) return ToSummary(execution, true);
        ScheduledVolumeBackup schedule =
            await dbContext.ScheduledVolumeBackupRepository.GetByIdAsync(execution.ScheduleId, cancellationToken)
            ?? throw new InvalidOperationException("Volume backup schedule was not found.");
        StorageOwnerContext? owner = owners.Load(schedule.OwnerKind, schedule.OwnerId);
        ResolvedTarget? target = owner is null ? null : ResolveTarget(owner, schedule);
        if (owner is null || target is null)
            return await SkipAsync(execution, "Backup skipped: the storage or its resource no longer exists.");
        Server server = dbContext.Servers.Single(item => item.Id == owner.ServerId);
        // Unattended runs on host-local servers need an instance administrator in the owning team; manual runs were
        // authorized for the requesting instance administrator when they were queued.
        if (!execution.Manual && !HostLocalExecutionGuard.IsAuthorized(authorization, server, null))
            return await SkipAsync(execution, HostLocalExecutionGuard.UnattendedNotAuthorizedMessage);

        TimeSpan timeout = TimeSpan.FromSeconds(schedule.TimeoutSeconds);
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            LockKey(schedule.TargetKind, schedule.TargetId), timeout + LockLeaseMargin, TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
            return await SkipAsync(execution,
                "Backup skipped: another backup or restore of this storage is running.");

        execution.Start(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        ServerExecutionTarget serverTarget = ServerExecutionTarget.From(server);
        try
        {
            VolumeArchiveSource source = ToSource(serverTarget, owner, target);
            if (!await runner.SourceExistsAsync(serverTarget, source, deadline.Token))
                throw new DomainException(
                    "The storage does not exist on the server yet; deploy the resource before backing it up.");

            VolumeArchive archive;
            IReadOnlyList<string> stopped = [];
            if (schedule.StopDuringBackup)
            {
                stopped = await runner.ListContainersUsingAsync(serverTarget, source, deadline.Token);
                // Recorded before stopping, so the recovery job can start them if this worker dies.
                execution.RecordContainersToStop(stopped.ToArray(), Now());
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            try
            {
                if (stopped.Count > 0) await runner.StopContainersAsync(serverTarget, stopped, deadline.Token);
                archive = await runner.ArchiveAsync(serverTarget, source, execution.Id.Value, timeout,
                    deadline.Token);
            }
            finally
            {
                if (stopped.Count > 0) await RestartAsync(execution, serverTarget, stopped);
            }

            await using (archive)
            {
                var key = $"volume/{schedule.Id.Value:D}/{Now():yyyyMMddHHmmss}-{execution.Id.Value:N}.tar.gz";
                execution.RecordArtifact(key, archive.SizeBytes, archive.Sha256, archive.Warning, Now());
                string? localBucket = null;
                if (!schedule.DisableLocalCopy)
                {
                    await UploadAsync(storage.Local.Storage, storage.Local.Bucket, key, archive, schedule, execution,
                        deadline.Token);
                    localBucket = storage.Local.Bucket;
                }

                var failedReplicas = 0;
                foreach (BackupDestination destination in LoadDestinations(schedule))
                    try
                    {
                        using IBackupDestinationClient client =
                            await connector.ConnectAsync(destination, deadline.Token);
                        await UploadAsync(client, destination.Bucket, key, archive, schedule, execution,
                            deadline.Token);
                        execution.RecordReplica(destination.Id, destination.Bucket, true, null, Now());
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failedReplicas++;
                        execution.RecordReplica(destination.Id, destination.Bucket, false,
                            redactor.Redact(exception.Message), Now());
                    }

                execution.Complete(localBucket, Now());
                await dbContext.SaveChangesAsync(CancellationToken.None);
                await ApplyRetentionAsync(schedule, CancellationToken.None);
                await auditWriter.RecordAsync(schedule.TeamId, null, StorageAuditActions.VolumeBackupCompleted,
                    new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
                    new Dictionary<string, object?>
                    {
                        ["scheduleId"] = schedule.Id.Value.ToString("D"),
                        ["sizeBytes"] = archive.SizeBytes,
                        ["failedReplicas"] = failedReplicas
                    }, CancellationToken.None);
                await notifications.PublishAsync(schedule.TeamId,
                    VolumeBackupNotifications.Succeeded(owner, schedule.Id.Value, execution.Id.Value, target.Name),
                    CancellationToken.None);
                if (failedReplicas > 0)
                {
                    schedule.RecordStored(Now());
                    await RecordFailureAsync(schedule, execution, owner, target, CancellationToken.None);
                }
                else if (schedule.RecordSuccess(Now()))
                {
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                    await notifications.PublishAsync(schedule.TeamId,
                        VolumeBackupNotifications.Recovered(owner, schedule.Id.Value, target.Name),
                        CancellationToken.None);
                }
                else
                {
                    await dbContext.SaveChangesAsync(CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            execution.Fail($"Backup exceeded its timeout of {timeout.TotalSeconds:0} seconds and was stopped.", Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await RecordFailureAsync(schedule, execution, owner, target, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.Fail(redactor.Redact(exception.Message), Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await RecordFailureAsync(schedule, execution, owner, target, CancellationToken.None);
        }

        return ToSummary(execution, true);
    }

    public async Task DeleteExecutionAsync(UserId actorUserId, TeamId teamId, VolumeBackupExecutionId executionId,
        bool deleteRemote, CancellationToken cancellationToken = default)
    {
        (VolumeBackupExecution execution, ScheduledVolumeBackup schedule, StorageOwnerContext owner) =
            await GetExecutionAsync(actorUserId, teamId, executionId, true, cancellationToken);
        if (IsBusy(execution))
            throw new VolumeBackupConflictException(
                "This backup is running, being restored or needs recovery; delete it after it finishes.");
        if (execution.Protected) throw new DomainException("Protected backups cannot be deleted.");
        var deleted = await DeleteCopiesAsync(execution, deleteRemote, cancellationToken);
        dbContext.VolumeBackupExecutionRepository.Remove(execution);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.VolumeBackupDeleted,
            new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["scheduleId"] = schedule.Id.Value.ToString("D"),
                ["deletedCopies"] = deleted,
                ["deleteRemote"] = deleteRemote
            }, cancellationToken);
    }

    // ---- Restores --------------------------------------------------------------------------------------------

    public async Task<VolumeBackupRestoreSummary> QueueRestoreAsync(UserId actorUserId, TeamId teamId,
        VolumeBackupExecutionId executionId, QueueVolumeRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        (VolumeBackupExecution execution, ScheduledVolumeBackup schedule, StorageOwnerContext owner) =
            await GetExecutionAsync(actorUserId, teamId, executionId, true, cancellationToken);
        RequireHostLocalAdmin(actorUserId, owner);
        if (!string.Equals(request.Confirmation, ConfirmationPhrase, StringComparison.Ordinal))
            throw new DomainException($"Restore confirmation must be {ConfirmationPhrase}.");
        if (!execution.HasRestorableArtifact) throw new DomainException("Only successful backups can be restored.");
        if (ResolveTarget(owner, schedule) is null)
            throw new DomainException("The storage of this backup no longer exists.");
        VolumeBackupExecutionId[] scheduleExecutions = dbContext.VolumeBackupExecutions
            .Where(item => item.ScheduleId == schedule.Id).Select(item => item.Id).ToArray();
        if (dbContext.VolumeBackupRestores.Any(restore => scheduleExecutions.Contains(restore.VolumeBackupExecutionId) &&
                                                          (restore.Status == VolumeRestoreStatus.Queued ||
                                                           restore.Status == VolumeRestoreStatus.Running)))
            throw new VolumeBackupConflictException("Another restore of this storage is already queued or running.");

        var restore = VolumeBackupRestore.Queue(owner.TeamId, execution.Id, actorUserId, request.ReplaceExisting,
            Now());
        restore.AppendLog(request.ReplaceExisting
            ? "Restore queued: the current content of the storage will be replaced."
            : "Restore queued: archived files will be written over the current content.", Now());
        await dbContext.VolumeBackupRestoreRepository.AddAsync(restore, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = jobs.Enqueue<RunVolumeRestoreJob>(job => job.RunAsync(restore.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(owner.TeamId, actorUserId, StorageAuditActions.VolumeRestoreQueued,
            new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["restoreId"] = restore.Id.Value.ToString("D"),
                ["replaceExisting"] = request.ReplaceExisting,
                ["jobId"] = jobId
            }, cancellationToken);
        return ToSummary(restore);
    }

    public async Task<VolumeBackupRestoreSummary> GetRestoreAsync(UserId actorUserId, TeamId teamId,
        VolumeBackupRestoreId restoreId, CancellationToken cancellationToken = default)
    {
        VolumeBackupRestore restore =
            await dbContext.VolumeBackupRestoreRepository.GetByIdAsync(restoreId, cancellationToken)
            ?? throw new InvalidOperationException("Restore was not found.");
        if (restore.TeamId != teamId) throw new InvalidOperationException("Restore was not found.");
        await GetExecutionAsync(actorUserId, teamId, restore.VolumeBackupExecutionId, false, cancellationToken);
        return ToSummary(restore);
    }

    public async Task<VolumeBackupRestoreSummary> RunRestoreAsync(VolumeBackupRestoreId restoreId,
        CancellationToken cancellationToken = default)
    {
        VolumeBackupRestore restore =
            await dbContext.VolumeBackupRestoreRepository.GetByIdAsync(restoreId, cancellationToken)
            ?? throw new InvalidOperationException("Restore was not found.");
        if (restore.Status != VolumeRestoreStatus.Queued) return ToSummary(restore);
        VolumeBackupExecution? execution =
            await dbContext.VolumeBackupExecutionRepository.GetByIdAsync(restore.VolumeBackupExecutionId,
                cancellationToken);
        ScheduledVolumeBackup? schedule = execution is null
            ? null
            : await dbContext.ScheduledVolumeBackupRepository.GetByIdAsync(execution.ScheduleId, cancellationToken);
        StorageOwnerContext? owner = schedule is null ? null : owners.Load(schedule.OwnerKind, schedule.OwnerId);
        ResolvedTarget? target = owner is null || schedule is null ? null : ResolveTarget(owner, schedule);
        if (execution is null || schedule is null || owner is null || target is null)
        {
            await FailRestoreAsync(restore, null, null, null, "The backup or its storage no longer exists.");
            return ToSummary(restore);
        }

        Server server = dbContext.Servers.Single(item => item.Id == owner.ServerId);
        if (!HostLocalExecutionGuard.IsAuthorized(authorization, server, restore.RequestedByUserId))
        {
            await FailRestoreAsync(restore, owner, schedule, target, HostLocalExecutionGuard.UnattendedNotAuthorizedMessage);
            return ToSummary(restore);
        }

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            LockKey(schedule.TargetKind, schedule.TargetId), RestoreTimeout + LockLeaseMargin, TimeSpan.Zero,
            cancellationToken);
        if (handle is null)
        {
            await FailRestoreAsync(restore, owner, schedule, target,
                "Another backup or restore of this storage is running. Nothing was changed.");
            return ToSummary(restore);
        }

        restore.Start(Now());
        restore.AppendLog("Restore started.", Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RestoreTimeout);
        ServerExecutionTarget serverTarget = ServerExecutionTarget.From(server);
        try
        {
            VolumeArchiveSource source = ToSource(serverTarget, owner, target);
            IReadOnlyList<string> containers =
                await runner.ListContainersUsingAsync(serverTarget, source, deadline.Token);
            restore.RecordContainersToStop(containers.ToArray(), Now());
            if (containers.Count > 0)
                restore.AppendLog($"Stopping {containers.Count} container(s) that use the storage.", Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            try
            {
                if (containers.Count > 0) await runner.StopContainersAsync(serverTarget, containers, deadline.Token);
                await using Stream artifact = await OpenArtifactAsync(execution, deadline.Token);
                await runner.RestoreAsync(serverTarget, source, artifact, execution.ChecksumSha256,
                    restore.ReplaceExisting, restore.Id.Value, RestoreTimeout,
                    async (line, token) =>
                    {
                        restore.AppendLog(redactor.Redact(line), Now());
                        await dbContext.SaveChangesAsync(token);
                    }, deadline.Token);
            }
            finally
            {
                if (containers.Count > 0)
                    try
                    {
                        await runner.StartContainersAsync(serverTarget, containers, CancellationToken.None);
                        restore.RecordContainersRestarted(Now());
                        restore.AppendLog("Started the stopped containers again.", Now());
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        restore.RecordRecoveryFailure(redactor.Redact(exception.Message), Now());
                        restore.AppendLog("Could not start the stopped containers; the recovery job retries.", Now());
                    }

                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

            restore.AppendLog("Restore completed.", Now());
            restore.Succeed(Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await auditWriter.RecordAsync(restore.TeamId, restore.RequestedByUserId,
                StorageAuditActions.VolumeRestoreCompleted,
                new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["restoreId"] = restore.Id.Value.ToString("D") },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            await FailRestoreAsync(restore, owner, schedule, target,
                $"Restore exceeded its timeout of {RestoreTimeout.TotalHours:0} hours and was stopped.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailRestoreAsync(restore, owner, schedule, target, redactor.Redact(exception.Message));
        }

        return ToSummary(restore);
    }

    // ---- Recovery and alerts ---------------------------------------------------------------------------------

    /// <summary>
    ///     Compensates interrupted runs (upstream <c>VolumeBackupRecoveryJob</c>): executions and restores that outlived
    ///     their deadline are failed; containers recorded as stopped are started again. Each item is retried at most
    ///     <see cref="VolumeBackupExecution.MaxRecoveryAttempts" /> times, no more often than
    ///     <see cref="RecoveryRetryInterval" /> and only within <see cref="RecoveryWindow" />; then it needs attention
    ///     and the team is notified once.
    /// </summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using DistributedLockHandle? guard = await locks.TryAcquireAsync(RecoveryLockKey,
            TimeSpan.FromMinutes(10), TimeSpan.Zero, cancellationToken);
        if (guard is null) return 0;
        DateTimeOffset now = Now();
        DateTimeOffset windowStart = now - RecoveryWindow;
        var handled = 0;

        VolumeBackupExecution[] candidates = dbContext.VolumeBackupExecutions
            .Where(execution => execution.CreatedAt >= windowStart &&
                                (execution.Status == VolumeBackupStatus.Queued ||
                                 execution.Status == VolumeBackupStatus.Running ||
                                 (execution.StopRecoveryPending && !execution.RecoveryNeedsAttention)))
            .ToArray();
        foreach (VolumeBackupExecution execution in candidates)
        {
            ScheduledVolumeBackup? schedule =
                await dbContext.ScheduledVolumeBackupRepository.GetByIdAsync(execution.ScheduleId, cancellationToken);
            if (schedule is null) continue;
            // A held target lock means the run is alive: never touch it.
            await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
                LockKey(schedule.TargetKind, schedule.TargetId), TimeSpan.FromMinutes(5), TimeSpan.Zero,
                cancellationToken);
            if (handle is null) continue;
            await dbContext.RefreshAsync(execution, cancellationToken);
            var deadline = (execution.StartedAt ?? execution.CreatedAt) +
                           TimeSpan.FromSeconds(schedule.TimeoutSeconds) + LockLeaseMargin;
            if (execution.Status == VolumeBackupStatus.Running && now > deadline)
            {
                execution.Fail("The backup was interrupted (the worker stopped before it finished).", now);
                handled++;
            }
            else if (execution.Status == VolumeBackupStatus.Queued && now - execution.CreatedAt > TimeSpan.FromDays(1))
            {
                execution.Fail("The backup never started.", now);
                handled++;
            }

            if (execution.StopRecoveryPending && !execution.RecoveryNeedsAttention && !execution.IsActive &&
                (execution.RecoveryLastAttemptAt is null || now - execution.RecoveryLastAttemptAt >= RecoveryRetryInterval))
            {
                StorageOwnerContext? owner = owners.Load(schedule.OwnerKind, schedule.OwnerId);
                if (owner is null) continue;
                Server server = dbContext.Servers.Single(item => item.Id == owner.ServerId);
                try
                {
                    await runner.StartContainersAsync(ServerExecutionTarget.From(server), execution.StoppedContainers,
                        cancellationToken);
                    execution.RecordContainersRestarted(now);
                    await auditWriter.RecordAsync(execution.TeamId, null, StorageAuditActions.VolumeBackupRecovered,
                        new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
                        new Dictionary<string, object?>(), cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    execution.RecordRecoveryFailure(redactor.Redact(exception.Message), now);
                    if (execution.RecoveryNeedsAttention)
                    {
                        await auditWriter.RecordAsync(execution.TeamId, null,
                            StorageAuditActions.VolumeBackupRecoveryFailed,
                            new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
                            new Dictionary<string, object?> { ["attempts"] = execution.RecoveryAttempts },
                            cancellationToken);
                        ResolvedTarget? target = ResolveTarget(owner, schedule);
                        await notifications.PublishAsync(execution.TeamId,
                            VolumeBackupNotifications.RecoveryNeedsAttention(owner, schedule.Id.Value,
                                execution.Id.Value, target?.Name ?? "storage"), cancellationToken);
                    }
                }

                handled++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        VolumeBackupRestore[] restores = dbContext.VolumeBackupRestores
            .Where(restore => restore.CreatedAt >= windowStart &&
                              (restore.Status == VolumeRestoreStatus.Running ||
                               (restore.StopRecoveryPending && !restore.RecoveryNeedsAttention)))
            .ToArray();
        foreach (VolumeBackupRestore restore in restores)
        {
            VolumeBackupExecution? execution =
                await dbContext.VolumeBackupExecutionRepository.GetByIdAsync(restore.VolumeBackupExecutionId,
                    cancellationToken);
            ScheduledVolumeBackup? schedule = execution is null
                ? null
                : await dbContext.ScheduledVolumeBackupRepository.GetByIdAsync(execution.ScheduleId,
                    cancellationToken);
            if (schedule is null) continue;
            await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
                LockKey(schedule.TargetKind, schedule.TargetId), TimeSpan.FromMinutes(5), TimeSpan.Zero,
                cancellationToken);
            if (handle is null) continue;
            if (restore.Status == VolumeRestoreStatus.Running &&
                now > (restore.StartedAt ?? restore.CreatedAt) + RestoreTimeout + LockLeaseMargin)
            {
                restore.AppendLog("The restore was interrupted (the worker stopped before it finished).", now);
                restore.Fail("The restore was interrupted.", now);
                handled++;
            }

            if (restore.StopRecoveryPending && !restore.RecoveryNeedsAttention && !restore.IsActive &&
                owners.Load(schedule.OwnerKind, schedule.OwnerId) is { } owner)
            {
                Server server = dbContext.Servers.Single(item => item.Id == owner.ServerId);
                try
                {
                    await runner.StartContainersAsync(ServerExecutionTarget.From(server), restore.StoppedContainers,
                        cancellationToken);
                    restore.RecordContainersRestarted(now);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    restore.RecordRecoveryFailure(redactor.Redact(exception.Message), now);
                    if (restore.RecoveryNeedsAttention)
                        await notifications.PublishAsync(restore.TeamId,
                            VolumeBackupNotifications.RecoveryNeedsAttention(owner, schedule.Id.Value,
                                restore.Id.Value, ResolveTarget(owner, schedule)?.Name ?? "storage"),
                            cancellationToken);
                }

                handled++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return handled;
    }

    /// <summary>Operator retry of a recovery that needs attention (fresh attempt budget, runs on the next pass).</summary>
    public async Task<VolumeBackupExecutionSummary> RetryRecoveryAsync(UserId actorUserId, TeamId teamId,
        VolumeBackupExecutionId executionId, CancellationToken cancellationToken = default)
    {
        (VolumeBackupExecution execution, _, StorageOwnerContext owner) =
            await GetExecutionAsync(actorUserId, teamId, executionId, true, cancellationToken);
        RequireHostLocalAdmin(actorUserId, owner);
        execution.ResetRecovery(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        jobs.Enqueue<VolumeBackupRecoveryJob>(job => job.RunAsync(CancellationToken.None));
        return ToSummary(execution, true);
    }

    /// <summary>One missing-backup alert per idle period (reuses the DATA-08 alert rules and event type).</summary>
    public async Task<int> CheckMissingBackupsAsync(CancellationToken cancellationToken = default)
    {
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(MissingCheckLockKey,
            TimeSpan.FromMinutes(10), TimeSpan.Zero, cancellationToken);
        if (handle is null) return 0;
        DateTimeOffset now = Now();
        var sent = 0;
        foreach (ScheduledVolumeBackup schedule in dbContext.ScheduledVolumeBackups
                     .Where(schedule => schedule.Enabled && schedule.MissingAlertDays > 0).AsEnumerable()
                     .Where(schedule => schedule.IsBackupMissing(now)).ToArray())
        {
            StorageOwnerContext? owner = owners.Load(schedule.OwnerKind, schedule.OwnerId);
            if (owner is null) continue;
            await notifications.PublishAsync(schedule.TeamId,
                VolumeBackupNotifications.Missing(owner, schedule.Id.Value,
                    ResolveTarget(owner, schedule)?.Name ?? "storage", schedule.MissingAlertDays), cancellationToken);
            schedule.RecordMissingAlertSent(now);
            sent++;
        }

        if (sent > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return sent;
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private sealed record ResolvedTarget(VolumeBackupTargetKind Kind, string Name, string Source);

    private (VolumeBackupTargetKind Kind, string Name) ResolveTargetForSchedule(StorageOwnerContext owner,
        Guid storageId)
    {
        var volumeId = new PersistentVolumeId(storageId);
        PersistentVolume? volume = dbContext.PersistentVolumes.SingleOrDefault(item =>
            item.Id == volumeId && item.OwnerKind == owner.Kind && item.OwnerId == owner.Id);
        if (volume is not null) return (VolumeBackupTargetKind.PersistentVolume, volume.Name);
        var mountId = new FileMountId(storageId);
        FileMount mount = dbContext.FileMounts.SingleOrDefault(item =>
                              item.Id == mountId && item.OwnerKind == owner.Kind && item.OwnerId == owner.Id)
                          ?? throw new InvalidOperationException("Storage was not found.");
        if (!mount.IsDirectory || mount.IsHostFile)
            throw new DomainException("Only volumes and directory mounts can be backed up.");
        return (VolumeBackupTargetKind.DirectoryMount, mount.Path);
    }

    /// <summary>The target of a schedule, or <see langword="null" /> when it was deleted.</summary>
    private ResolvedTarget? ResolveTarget(StorageOwnerContext owner, ScheduledVolumeBackup schedule)
    {
        if (schedule.TargetKind == VolumeBackupTargetKind.PersistentVolume)
        {
            var volumeId = new PersistentVolumeId(schedule.TargetId);
            PersistentVolume? volume = dbContext.PersistentVolumes.SingleOrDefault(item =>
                item.Id == volumeId && item.OwnerKind == owner.Kind && item.OwnerId == owner.Id);
            return volume is null
                ? null
                : new ResolvedTarget(VolumeBackupTargetKind.PersistentVolume, volume.Name, volume.RuntimeName);
        }

        var mountId = new FileMountId(schedule.TargetId);
        FileMount? mount = dbContext.FileMounts.SingleOrDefault(item =>
            item.Id == mountId && item.OwnerKind == owner.Kind && item.OwnerId == owner.Id);
        return mount is null || !mount.IsDirectory
            ? null
            : new ResolvedTarget(VolumeBackupTargetKind.DirectoryMount, mount.Path,
                $"{StoragePaths.ResourceDirectory(owner.Kind, owner.Id)}/{mount.Path}");
    }

    private VolumeArchiveSource ToSource(ServerExecutionTarget server, StorageOwnerContext owner,
        ResolvedTarget target)
    {
        // Directory mounts resolve through the server's file channel: owned root, validated, symlink-checked.
        return target.Kind == VolumeBackupTargetKind.PersistentVolume
            ? new VolumeArchiveSource(target.Kind, target.Source)
            : new VolumeArchiveSource(target.Kind, channels.For(server).ResolveHostPath(server, target.Source));
    }

    private void RequireHostLocalAdmin(UserId actorUserId, StorageOwnerContext owner)
    {
        Server server = dbContext.Servers.Single(item => item.Id == owner.ServerId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
    }

    private async Task RestartAsync(VolumeBackupExecution execution, ServerExecutionTarget server,
        IReadOnlyList<string> containers)
    {
        try
        {
            await runner.StartContainersAsync(server, containers, CancellationToken.None);
            execution.RecordContainersRestarted(Now());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The recovery job retries with bounded attempts.
            execution.RecordRecoveryFailure(redactor.Redact(exception.Message), Now());
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<VolumeBackupExecutionSummary> SkipAsync(VolumeBackupExecution execution, string reason)
    {
        execution.Skip(reason, Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        return ToSummary(execution, true);
    }

    private async Task RecordFailureAsync(ScheduledVolumeBackup schedule, VolumeBackupExecution execution,
        StorageOwnerContext owner, ResolvedTarget target, CancellationToken cancellationToken)
    {
        var notify = schedule.RecordFailure(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(schedule.TeamId, null, StorageAuditActions.VolumeBackupFailed,
            new AuditTarget("volume-backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["scheduleId"] = schedule.Id.Value.ToString("D") }, cancellationToken);
        if (notify)
            await notifications.PublishAsync(schedule.TeamId,
                VolumeBackupNotifications.Failed(owner, schedule.Id.Value, execution.Id.Value, target.Name),
                cancellationToken);
    }

    private async Task FailRestoreAsync(VolumeBackupRestore restore, StorageOwnerContext? owner,
        ScheduledVolumeBackup? schedule, ResolvedTarget? target, string reason)
    {
        restore.AppendLog($"Restore failed: {reason}", Now());
        restore.Fail(reason, Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await auditWriter.RecordAsync(restore.TeamId, restore.RequestedByUserId,
            StorageAuditActions.VolumeRestoreFailed,
            new AuditTarget("volume-backup", restore.VolumeBackupExecutionId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["restoreId"] = restore.Id.Value.ToString("D") },
            CancellationToken.None);
        if (owner is not null && schedule is not null)
            await notifications.PublishAsync(restore.TeamId,
                VolumeBackupNotifications.RestoreFailed(owner, schedule.Id.Value, restore.Id.Value,
                    target?.Name ?? "storage"), CancellationToken.None);
    }

    private static async Task UploadAsync(IObjectStorage target, string bucket, string key, VolumeArchive archive,
        ScheduledVolumeBackup schedule, VolumeBackupExecution execution, CancellationToken cancellationToken)
    {
        await using Stream content = archive.OpenRead();
        await target.PutAsync(new ObjectStoragePutRequest(new ObjectStorageKey(bucket, key), content,
            "application/gzip",
            new Dictionary<string, string>
            {
                ["vessel.volumeBackupScheduleId"] = schedule.Id.Value.ToString("D"),
                ["vessel.volumeBackupExecutionId"] = execution.Id.Value.ToString("D"),
                ["vessel.format"] = "tar+gzip",
                ["vessel.sha256"] = archive.Sha256
            }, archive.SizeBytes), cancellationToken);
    }

    private async Task<Stream> OpenArtifactAsync(VolumeBackupExecution execution, CancellationToken cancellationToken)
    {
        if (execution.ArtifactKey is null) throw new DomainException("The backup has no archive.");
        if (execution.HasLocalCopy)
            return await storage.Local.Storage.OpenReadAsync(
                new ObjectStorageKey(execution.ArtifactBucket!, execution.ArtifactKey), cancellationToken);
        foreach (VolumeBackupReplica replica in execution.Replicas.Where(replica => replica.IsAvailable))
        {
            BackupDestination? destination = dbContext.BackupDestinations.SingleOrDefault(item =>
                item.Id == replica.BackupDestinationId && item.TeamId == execution.TeamId);
            if (destination is null) continue;
            IBackupDestinationClient client = await connector.ConnectAsync(destination, cancellationToken);
            try
            {
                Stream content = await client.OpenReadAsync(new ObjectStorageKey(replica.Bucket, execution.ArtifactKey),
                    cancellationToken);
                return new DisposingStream(content, client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw new DomainException("No copy of this backup is available any more.");
    }

    private async Task ApplyRetentionAsync(ScheduledVolumeBackup schedule, CancellationToken cancellationToken)
    {
        DateTimeOffset now = Now();
        HashSet<VolumeBackupExecutionId> restoring = dbContext.VolumeBackupRestores
            .Where(restore => restore.Status == VolumeRestoreStatus.Queued ||
                              restore.Status == VolumeRestoreStatus.Running)
            .Select(restore => restore.VolumeBackupExecutionId).ToHashSet();
        VolumeBackupExecution[] candidates = dbContext.VolumeBackupExecutions
            .Where(execution => execution.ScheduleId == schedule.Id && !execution.Protected &&
                                execution.Status == VolumeBackupStatus.Succeeded)
            .OrderByDescending(execution => execution.CreatedAt).AsEnumerable()
            .Where(execution => !restoring.Contains(execution.Id)).ToArray();
        var pruned = 0;
        foreach (VolumeBackupExecution execution in schedule.LocalRetention.SelectExpired(
                     candidates.Where(item => item.HasLocalCopy).ToArray(), item => item.CreatedAt,
                     item => item.SizeBytes ?? 0, now))
        {
            await storage.Local.Storage.DeleteAsync(
                new ObjectStorageKey(execution.ArtifactBucket!, execution.ArtifactKey!), cancellationToken);
            execution.MarkLocalCopyDeleted(now);
            pruned++;
        }

        foreach (BackupDestination destination in LoadDestinations(schedule))
        {
            IReadOnlyList<VolumeBackupExecution> expired = schedule.RemoteRetention.SelectExpired(
                candidates.Where(item => item.Replicas.Any(replica =>
                    replica.BackupDestinationId == destination.Id && replica.IsAvailable)).ToArray(),
                item => item.CreatedAt, item => item.SizeBytes ?? 0, now);
            if (expired.Count == 0) continue;
            try
            {
                using IBackupDestinationClient client = await connector.ConnectAsync(destination, cancellationToken);
                foreach (VolumeBackupExecution execution in expired)
                {
                    VolumeBackupReplica replica = execution.Replicas.First(item =>
                        item.BackupDestinationId == destination.Id && item.IsAvailable);
                    await client.DeleteAsync(new ObjectStorageKey(replica.Bucket, execution.ArtifactKey!),
                        cancellationToken);
                    execution.MarkReplicaDeleted(destination.Id, now);
                    pruned++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Remote pruning retries on the next successful run.
            }
        }

        if (pruned == 0) return;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(schedule.TeamId, null, StorageAuditActions.VolumeBackupPruned,
            new AuditTarget("volume-backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["prunedCopies"] = pruned }, cancellationToken);
    }

    private async Task<int> DeleteCopiesAsync(VolumeBackupExecution execution, bool deleteRemote,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        if (execution.HasLocalCopy)
        {
            await storage.Local.Storage.DeleteAsync(
                new ObjectStorageKey(execution.ArtifactBucket!, execution.ArtifactKey!), cancellationToken);
            execution.MarkLocalCopyDeleted(Now());
            deleted++;
        }

        if (!deleteRemote || execution.ArtifactKey is null) return deleted;
        foreach (VolumeBackupReplica replica in execution.Replicas.Where(replica => replica.IsAvailable).ToArray())
        {
            BackupDestination? destination = dbContext.BackupDestinations.SingleOrDefault(item =>
                item.Id == replica.BackupDestinationId && item.TeamId == execution.TeamId);
            if (destination is null) continue;
            using IBackupDestinationClient client = await connector.ConnectAsync(destination, cancellationToken);
            await client.DeleteAsync(new ObjectStorageKey(replica.Bucket, execution.ArtifactKey), cancellationToken);
            execution.MarkReplicaDeleted(destination.Id, Now());
            deleted++;
        }

        return deleted;
    }

    private bool IsBusy(VolumeBackupExecution execution)
    {
        return execution.IsActive || execution.StopRecoveryPending ||
               dbContext.VolumeBackupRestores.Any(restore => restore.VolumeBackupExecutionId == execution.Id &&
                                                             (restore.Status == VolumeRestoreStatus.Queued ||
                                                              restore.Status == VolumeRestoreStatus.Running ||
                                                              restore.StopRecoveryPending));
    }

    private BackupDestination[] LoadDestinations(ScheduledVolumeBackup schedule)
    {
        if (schedule.Destinations.Count == 0) return [];
        BackupDestinationId[] ids = schedule.DestinationIds.ToArray();
        return dbContext.BackupDestinations
            .Where(destination => destination.TeamId == schedule.TeamId && ids.Contains(destination.Id)).ToArray();
    }

    private BackupDestinationId[] ResolveDestinations(TeamId teamId, IReadOnlyList<Guid>? ids)
    {
        if (ids is null || ids.Count == 0) return [];
        BackupDestinationId[] requested = ids.Distinct().Select(id => new BackupDestinationId(id)).ToArray();
        var found = dbContext.BackupDestinations
            .Count(destination => destination.TeamId == teamId && requested.Contains(destination.Id));
        // Unknown and other teams' destinations are reported the same way, so IDs cannot be probed.
        if (found != requested.Length) throw new DomainException("Backup destination was not found.");
        return requested;
    }

    private static BackupRetentionPolicy ToRetention(BackupRetentionRequest? request, int defaultCount)
    {
        if (request is null) return new BackupRetentionPolicy(defaultCount, 0, 0);
        if (request.MaxStorageGigabytes is < 0 or > 1_000_000)
            throw new DomainException("Backup retention size must be between 0 and 1,000,000 GB.");
        return new BackupRetentionPolicy(request.Count, request.Days,
            (long)decimal.Round(request.MaxStorageGigabytes * 1024m * 1024m * 1024m));
    }

    private void SyncRecurringJob(ScheduledVolumeBackup schedule)
    {
        if (schedule.Enabled)
            recurringJobs.AddOrUpdate<RunVolumeBackupJob>(RecurringJobId(schedule.Id),
                job => job.RunAsync(schedule.Id.Value, CancellationToken.None), schedule.CronExpression);
        else
            recurringJobs.RemoveIfExists(RecurringJobId(schedule.Id));
    }

    private async Task<(ScheduledVolumeBackup, StorageOwnerContext)> GetScheduleAsync(UserId actorUserId,
        TeamId teamId, ScheduledVolumeBackupId scheduleId, bool write, CancellationToken cancellationToken)
    {
        ScheduledVolumeBackup? schedule =
            await dbContext.ScheduledVolumeBackupRepository.GetByIdAsync(scheduleId, cancellationToken);
        if (schedule is null || schedule.TeamId != teamId)
            throw new InvalidOperationException("Volume backup schedule was not found.");
        StorageOwnerContext owner = owners.Authorize(actorUserId, teamId, schedule.OwnerKind, schedule.OwnerId, write);
        return (schedule, owner);
    }

    private async Task<(VolumeBackupExecution, ScheduledVolumeBackup, StorageOwnerContext)> GetExecutionAsync(
        UserId actorUserId, TeamId teamId, VolumeBackupExecutionId executionId, bool write,
        CancellationToken cancellationToken)
    {
        VolumeBackupExecution? execution =
            await dbContext.VolumeBackupExecutionRepository.GetByIdAsync(executionId, cancellationToken);
        if (execution is null || execution.TeamId != teamId)
            throw new InvalidOperationException("Volume backup was not found.");
        (ScheduledVolumeBackup schedule, StorageOwnerContext owner) =
            await GetScheduleAsync(actorUserId, teamId, execution.ScheduleId, write, cancellationToken);
        return (execution, schedule, owner);
    }

    private Task AuditAsync(TeamId teamId, UserId actorUserId, string action, ScheduledVolumeBackup schedule,
        CancellationToken cancellationToken)
    {
        return auditWriter.RecordAsync(teamId, actorUserId, action,
            new AuditTarget("volume-backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["targetKind"] = schedule.TargetKind.ToString(),
                ["targetId"] = schedule.TargetId.ToString("D"),
                ["enabled"] = schedule.Enabled,
                ["destinations"] = schedule.Destinations.Count
            }, cancellationToken);
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    public static VolumeBackupScheduleSummary ToSummary(ScheduledVolumeBackup schedule)
    {
        return new VolumeBackupScheduleSummary(schedule.Id.Value, schedule.OwnerKind, schedule.OwnerId,
            schedule.TargetKind, schedule.TargetId, schedule.CronExpression, schedule.Enabled,
            schedule.StopDuringBackup, schedule.DisableLocalCopy, schedule.TimeoutSeconds,
            new BackupRetentionSummary(schedule.LocalRetentionCount, schedule.LocalRetentionDays,
                schedule.LocalRetentionMaxBytes),
            new BackupRetentionSummary(schedule.RemoteRetentionCount, schedule.RemoteRetentionDays,
                schedule.RemoteRetentionMaxBytes),
            schedule.DestinationIds.Select(id => id.Value).ToArray(), schedule.MissingAlertDays, schedule.LastRunAt,
            schedule.LastSucceededAt, schedule.FailingSince);
    }

    public static VolumeBackupExecutionSummary ToSummary(VolumeBackupExecution execution, bool includeSensitive)
    {
        var hasMessage = execution.FailureReason is not null || execution.RecoveryError is not null ||
                         execution.Replicas.Any(replica => replica.Message is not null);
        return new VolumeBackupExecutionSummary(execution.Id.Value, execution.ScheduleId.Value, execution.Status,
            execution.Manual, execution.SizeBytes, execution.ChecksumSha256, execution.HasLocalCopy,
            execution.Replicas.Select(replica => new BackupReplicaSummary(replica.BackupDestinationId?.Value,
                replica.Bucket, replica.Uploaded, replica.DeletedAt is not null,
                includeSensitive ? replica.Message : null, replica.CreatedAt)).ToArray(),
            includeSensitive ? execution.FailureReason : null, execution.Warning, execution.StopRecoveryPending,
            execution.RecoveryAttempts, execution.RecoveryNeedsAttention,
            includeSensitive ? execution.RecoveryError : null, execution.CreatedAt, execution.StartedAt,
            execution.FinishedAt, !includeSensitive && hasMessage);
    }

    public static VolumeBackupRestoreSummary ToSummary(VolumeBackupRestore restore)
    {
        return new VolumeBackupRestoreSummary(restore.Id.Value, restore.VolumeBackupExecutionId.Value,
            restore.Status, restore.ReplaceExisting, restore.FailureReason, restore.StopRecoveryPending,
            restore.RecoveryNeedsAttention, restore.CreatedAt, restore.StartedAt, restore.FinishedAt, restore.Log);
    }

    /// <summary>Disposes the destination client together with the download stream.</summary>
    private sealed class DisposingStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A volume backup or restore conflicts with another operation on the same storage (HTTP 409).</summary>
public sealed class VolumeBackupConflictException(string message) : InvalidOperationException(message);

public sealed class RunVolumeBackupJob(VolumeBackupService service)
{
    public Task RunAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        return service.RunScheduledAsync(new ScheduledVolumeBackupId(scheduleId), cancellationToken);
    }
}

public sealed class RunVolumeBackupExecutionJob(VolumeBackupService service)
{
    public Task RunAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        return service.RunExecutionAsync(new VolumeBackupExecutionId(executionId), cancellationToken);
    }
}

public sealed class RunVolumeRestoreJob(VolumeBackupService service)
{
    public Task RunAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        return service.RunRestoreAsync(new VolumeBackupRestoreId(restoreId), cancellationToken);
    }
}

public sealed class VolumeBackupRecoveryJob(VolumeBackupService service)
{
    public const string RecurringJobId = "volume-backups:recovery";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return service.RecoverAsync(cancellationToken);
    }
}

public sealed class CheckMissingVolumeBackupsJob(VolumeBackupService service)
{
    public const string RecurringJobId = "volume-backups:missing-check";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return service.CheckMissingBackupsAsync(cancellationToken);
    }
}
