using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.ManagedServices;
using Vessel.Application.Notifications;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Security;
using Vessel.Application.Storage;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Backups;
using Vessel.Domain.Common;
using Vessel.Domain.Databases;
using Vessel.Domain.Servers;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Backups;

/// <summary>
///     Database backup schedules and executions: create, edit, enable/disable and delete schedules; queue and run
///     backups under the shared database operation lock; store the local copy and remote replicas; apply local and
///     remote retention; raise throttled notifications; list, delete and download executions.
/// </summary>
public sealed class DatabaseBackupService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IBackgroundJobDispatcher jobs,
    IRecurringJobScheduler recurringJobs,
    IDistributedLockManager locks,
    ISecretVault secretVault,
    ISecretRedactor redactor,
    IDatabaseBackupProvider backupProvider,
    IBackupStorageProvider storage,
    BackupDestinationConnector connector,
    BackupArtifactReader artifacts,
    IAuditWriter auditWriter,
    INotificationEventPublisher notifications,
    TimeProvider timeProvider)
{
    public const int DefaultManualRetentionCount = 10;

    public const int MaxPageSize = 100;

    public const string MissingCheckLockKey = "backups:missing-check";

    private static readonly TimeSpan LockLeaseMargin = TimeSpan.FromMinutes(10);

    public static string RecurringJobId(BackupScheduleId scheduleId)
    {
        return $"backup:{scheduleId.Value:D}";
    }

    // ---- Schedules -------------------------------------------------------------------------------------------

    public IReadOnlyList<BackupScheduleSummary> ListSchedules(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsRead);
        return dbContext.BackupSchedules
            .Where(schedule => schedule.TeamId == teamId && schedule.DatabaseResourceId == databaseId)
            .OrderBy(schedule => schedule.CreatedAt)
            .AsEnumerable()
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<BackupScheduleSummary> CreateScheduleAsync(UserId actorUserId, TeamId teamId,
        CreateBackupScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var databaseId = new DatabaseResourceId(request.DatabaseId);
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        DatabaseResource database = await GetDatabaseAsync(databaseId, cancellationToken);
        var schedule = BackupSchedule.Create(teamId, database.Id, new ResourceName(request.Name),
            request.CronExpression, request.RetentionCount, BackupStorageKind.Local, Now(),
            request.TimeoutSeconds ?? BackupSchedule.DefaultTimeoutSeconds);
        IReadOnlyList<BackupDestinationId> destinations = ResolveDestinations(teamId, request.DestinationIds);
        schedule.Configure(schedule.Name, request.CronExpression, ToSelection(request.DumpAll, request.Databases),
            request.DumpFormat, ToRetention(request.LocalRetention, request.RetentionCount),
            ToRetention(request.RemoteRetention, 0), destinations, request.DisableLocalCopy,
            request.TimeoutSeconds ?? BackupSchedule.DefaultTimeoutSeconds, request.MissingAlertDays, Now());
        if (!request.Enabled) schedule.Disable(Now());
        await dbContext.BackupScheduleRepository.AddAsync(schedule, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        SyncRecurringJob(schedule);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupScheduleCreated,
            new AuditTarget("backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["databaseId"] = database.Id.Value.ToString("D"),
                ["destinations"] = destinations.Count,
                ["enabled"] = schedule.Enabled
            }, cancellationToken);
        return ToSummary(schedule);
    }

    public async Task<BackupScheduleSummary> UpdateScheduleAsync(UserId actorUserId, TeamId teamId,
        BackupScheduleId scheduleId, UpdateBackupScheduleRequest request, CancellationToken cancellationToken = default)
    {
        BackupSchedule schedule = await GetScheduleAsync(actorUserId, teamId, scheduleId,
            VesselPermissions.ProjectsWrite, cancellationToken);
        BackupDatabaseSelection selection = request.DumpAll is null && request.Databases is null
            ? schedule.Databases
            : ToSelection(request.DumpAll ?? false, request.Databases);
        IReadOnlyList<BackupDestinationId> destinations = request.DestinationIds is null
            ? schedule.DestinationIds
            : ResolveDestinations(teamId, request.DestinationIds);
        schedule.Configure(
            request.Name is null ? schedule.Name : new ResourceName(request.Name),
            request.CronExpression ?? schedule.CronExpression,
            selection,
            request.DumpFormat ?? schedule.DumpFormat,
            request.LocalRetention is null
                ? schedule.LocalRetention
                : ToRetention(request.LocalRetention, schedule.RetentionCount),
            request.RemoteRetention is null ? schedule.RemoteRetention : ToRetention(request.RemoteRetention, 0),
            destinations,
            request.DisableLocalCopy ?? schedule.DisableLocalCopy,
            request.TimeoutSeconds ?? schedule.TimeoutSeconds,
            request.MissingAlertDays ?? schedule.MissingAlertDays,
            Now());
        if (request.Enabled == true) schedule.Enable(Now());
        if (request.Enabled == false) schedule.Disable(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        SyncRecurringJob(schedule);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupScheduleUpdated,
            new AuditTarget("backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["databaseId"] = schedule.DatabaseResourceId.Value.ToString("D"),
                ["enabled"] = schedule.Enabled
            }, cancellationToken);
        return ToSummary(schedule);
    }

    /// <summary>
    ///     Deletes a schedule and (through the cascading foreign key) its executions. With
    ///     <paramref name="deleteArtifacts" /> the local copies and remote replicas are deleted first; otherwise the
    ///     files stay on disk and in the buckets but are no longer tracked.
    /// </summary>
    public async Task DeleteScheduleAsync(UserId actorUserId, TeamId teamId, BackupScheduleId scheduleId,
        bool deleteArtifacts, CancellationToken cancellationToken = default)
    {
        BackupSchedule schedule = await GetScheduleAsync(actorUserId, teamId, scheduleId,
            VesselPermissions.ProjectsWrite, cancellationToken);
        BackupExecution[] executions = dbContext.BackupExecutions
            .Where(execution => execution.ScheduleId == schedule.Id).ToArray();
        if (executions.Any(IsInFlight))
            throw new DatabaseOperationConflictException(
                "A backup or restore of this schedule is running; delete the schedule after it finishes.");
        recurringJobs.RemoveIfExists(RecurringJobId(schedule.Id));
        var deleted = 0;
        if (deleteArtifacts)
            foreach (BackupExecution execution in executions)
                deleted += await DeleteCopiesAsync(execution, true, cancellationToken);

        foreach (BackupExecution execution in executions) dbContext.BackupExecutionRepository.Remove(execution);
        dbContext.BackupScheduleRepository.Remove(schedule);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupScheduleDeleted,
            new AuditTarget("backup-schedule", schedule.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["databaseId"] = schedule.DatabaseResourceId.Value.ToString("D"),
                ["executions"] = executions.Length,
                ["deletedCopies"] = deleted,
                ["deleteArtifacts"] = deleteArtifacts
            }, cancellationToken);
    }

    // ---- Executions ------------------------------------------------------------------------------------------

    public PagedBackupExecutions ListExecutions(UserId actorUserId, TeamId teamId, DatabaseResourceId databaseId,
        BackupScheduleId? scheduleId, int page, int pageSize)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsRead);
        var sensitive = authorization.HasPermission(actorUserId, teamId, VesselPermissions.SecretsRead);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        IQueryable<BackupExecution> query = dbContext.BackupExecutions.Where(execution =>
            execution.TeamId == teamId && execution.DatabaseResourceId == databaseId);
        if (scheduleId is { } id) query = query.Where(execution => execution.ScheduleId == id);
        var total = query.Count();
        BackupExecutionSummary[] items = query.OrderByDescending(execution => execution.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsEnumerable()
            .Select(execution => ToSummary(execution, sensitive))
            .ToArray();
        return new PagedBackupExecutions(items, page, pageSize, total);
    }

    public PagedBackupExecutions ListScheduleExecutions(UserId actorUserId, TeamId teamId,
        BackupScheduleId scheduleId, int page, int pageSize)
    {
        BackupSchedule schedule = dbContext.BackupSchedules.SingleOrDefault(item => item.Id == scheduleId)
                                  ?? throw new InvalidOperationException("Backup schedule was not found.");
        if (schedule.TeamId != teamId) throw new UnauthorizedAccessException("Backup schedule is outside the active team.");
        return ListExecutions(actorUserId, teamId, schedule.DatabaseResourceId, scheduleId, page, pageSize);
    }

    public async Task<BackupExecutionSummary> QueueBackupAsync(UserId actorUserId, TeamId teamId,
        DatabaseResourceId databaseId, QueueBackupRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(databaseId), VesselPermissions.ProjectsWrite);
        DatabaseResource database = await GetDatabaseAsync(databaseId, cancellationToken);
        var execution = BackupExecution.Queue(teamId, database.Id, null, BackupStorageKind.Local, Now(),
            request?.DatabaseName);
        await dbContext.BackupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        var jobId = jobs.Enqueue<RunBackupExecutionJob>(job =>
            job.RunAsync(execution.Id.Value, CancellationToken.None));
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupQueued,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["jobId"] = jobId }, cancellationToken);
        return ToSummary(execution, true);
    }

    /// <summary>
    ///     Deletes one execution record and its local copy; remote replicas are deleted only with
    ///     <paramref name="deleteRemote" />.
    /// </summary>
    public async Task DeleteExecutionAsync(UserId actorUserId, TeamId teamId, BackupExecutionId executionId,
        bool deleteRemote, CancellationToken cancellationToken = default)
    {
        BackupExecution execution = await GetExecutionAsync(teamId, executionId, cancellationToken);
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(execution.DatabaseResourceId),
            VesselPermissions.ProjectsWrite);
        if (IsInFlight(execution))
            throw new DatabaseOperationConflictException(
                "This backup is running or being restored; delete it after it finishes.");
        if (execution.Protected) throw new DomainException("Protected backups cannot be deleted.");
        var deleted = await DeleteCopiesAsync(execution, deleteRemote, cancellationToken);
        dbContext.BackupExecutionRepository.Remove(execution);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupDeleted,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["databaseId"] = execution.DatabaseResourceId.Value.ToString("D"),
                ["deleteRemote"] = deleteRemote,
                ["deletedCopies"] = deleted
            }, cancellationToken);
    }

    /// <summary>
    ///     Opens a backup artifact for streaming download. Requires read access to the database and sensitive read
    ///     access (<c>secrets.read</c>): a dump contains every row and credential stored in the database.
    /// </summary>
    public async Task<BackupArtifactDownload> OpenDownloadAsync(UserId actorUserId, TeamId teamId,
        BackupExecutionId executionId, CancellationToken cancellationToken = default)
    {
        BackupExecution execution = await GetExecutionAsync(teamId, executionId, cancellationToken);
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(execution.DatabaseResourceId),
            VesselPermissions.ProjectsRead);
        if (!authorization.HasPermission(actorUserId, teamId, VesselPermissions.SecretsRead))
            throw new UnauthorizedAccessException(
                $"Downloading backups requires the '{VesselPermissions.SecretsRead}' permission.");
        if (!execution.HasRestorableArtifact) throw new DomainException("This backup has no downloadable artifact.");
        Stream content = await artifacts.OpenAsync(execution, cancellationToken);
        await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.BackupDownloaded,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?>
            {
                ["databaseId"] = execution.DatabaseResourceId.Value.ToString("D"),
                ["sizeBytes"] = execution.SizeBytes
            }, cancellationToken);
        var key = execution.ArtifactKey!;
        var objectName = key[(key.LastIndexOf('/') + 1)..];
        var dot = objectName.IndexOf('.', StringComparison.Ordinal);
        var extension = dot >= 0 ? objectName[dot..] : string.Empty;
        var fileName = $"{execution.DatabaseName ?? "database"}-{execution.CreatedAt:yyyyMMddHHmmss}{extension}";
        var contentType = key.EndsWith(".gz", StringComparison.Ordinal) ? "application/gzip" : "application/octet-stream";
        return new BackupArtifactDownload(content, fileName, contentType, execution.SizeBytes);
    }

    // ---- Running backups -------------------------------------------------------------------------------------

    /// <summary>
    ///     Runs one scheduled backup. A missing or disabled schedule never runs (its recurring job is removed). The
    ///     database selection expands into one execution per database where the engine needs it.
    /// </summary>
    public async Task<IReadOnlyList<BackupExecutionSummary>> RunScheduledBackupAsync(BackupScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        BackupSchedule? schedule =
            await dbContext.BackupScheduleRepository.GetByIdAsync(scheduleId, cancellationToken);
        if (schedule is null || !schedule.Enabled)
        {
            recurringJobs.RemoveIfExists(RecurringJobId(scheduleId));
            return [];
        }

        DatabaseResource database = await GetDatabaseAsync(schedule.DatabaseResourceId, cancellationToken);
        IReadOnlyList<string?> names = await ResolveDatabaseNamesAsync(schedule, database, cancellationToken);
        schedule.RecordRun(Now());
        var executions = names.Select(name => BackupExecution.Queue(schedule.TeamId, schedule.DatabaseResourceId,
            schedule.Id, schedule.StorageKind, Now(), name)).ToArray();
        foreach (BackupExecution execution in executions)
            await dbContext.BackupExecutionRepository.AddAsync(execution, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var results = new List<BackupExecutionSummary>();
        foreach (BackupExecution execution in executions)
            results.Add(await RunBackupExecutionAsync(execution.Id, cancellationToken));
        return results;
    }

    public async Task<BackupExecutionSummary> RunBackupExecutionAsync(BackupExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        BackupExecution execution =
            await dbContext.BackupExecutionRepository.GetByIdAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException("Backup execution was not found.");
        // Retry-safe: a Hangfire retry of an execution that already ran must not dump again.
        if (execution.Status != BackupExecutionStatus.Queued) return ToSummary(execution, true);
        DatabaseResource database = await GetDatabaseAsync(execution.DatabaseResourceId, cancellationToken);
        BackupSchedule? schedule = execution.ScheduleId is { } scheduleId
            ? await dbContext.BackupScheduleRepository.GetByIdAsync(scheduleId, cancellationToken)
            : null;
        TimeSpan timeout = TimeSpan.FromSeconds(schedule?.TimeoutSeconds ?? BackupSchedule.DefaultTimeoutSeconds);

        if (database.LifecycleState != DatabaseLifecycleState.Running)
            return await SkipAsync(execution, database,
                $"Backup skipped: the database is {database.LifecycleState}, not running.", cancellationToken);

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            DatabaseOperationLocks.KeyFor(database.Id), timeout + LockLeaseMargin, TimeSpan.Zero, cancellationToken);
        if (handle is null)
            return await SkipAsync(execution, database,
                "Backup skipped: another lifecycle, backup or restore operation is running for this database.",
                cancellationToken);

        execution.Start(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        string? credentials = null;
        try
        {
            Server server = dbContext.Servers.Single(item => item.Id == database.ServerId);
            credentials = await secretVault.RevealForDeploymentAsync(execution.TeamId,
                database.CredentialsReferenceId, deadline.Token);
            await using BackupArtifactFile artifact = await backupProvider.BackupAsync(
                new DatabaseBackupRequest(database, ManagedRuntimeTargets.For(server), credentials, execution.Id,
                    execution.DatabaseName, schedule?.DumpFormat ?? BackupDumpFormat.Native),
                deadline.Token);
            execution.RecordArtifact(artifact.Key, artifact.SizeBytes, artifact.Sha256, artifact.Format, Now());

            string? localBucket = null;
            if (schedule is not { DisableLocalCopy: true })
            {
                await UploadAsync(storage.Local.Storage, storage.Local.Bucket, artifact, database, execution,
                    deadline.Token);
                localBucket = storage.Local.Bucket;
            }

            var failedReplicas = 0;
            foreach (BackupDestination destination in LoadDestinations(schedule))
                try
                {
                    using IBackupDestinationClient client =
                        await connector.ConnectAsync(destination, deadline.Token);
                    await UploadAsync(client, destination.Bucket, artifact, database, execution, deadline.Token);
                    execution.RecordReplica(destination.Id, destination.Bucket, true, null, Now());
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failedReplicas++;
                    execution.RecordReplica(destination.Id, destination.Bucket, false,
                        Redact(exception.Message, credentials), Now());
                }

            execution.Complete(localBucket, Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await ApplyRetentionAsync(schedule, execution, cancellationToken);
            await auditWriter.RecordAsync(execution.TeamId, null, AuditActions.BackupCompleted,
                new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["databaseId"] = database.Id.Value.ToString("D"),
                    ["sizeBytes"] = artifact.SizeBytes,
                    ["format"] = artifact.Format,
                    ["failedReplicas"] = failedReplicas
                },
                CancellationToken.None);
            await notifications.PublishAsync(execution.TeamId,
                OperationalNotifications.BackupSucceeded(execution.Id.Value, database.Id.Value, database.Name.Value),
                CancellationToken.None);
            if (failedReplicas > 0)
                await RecordFailureAsync(schedule, execution, database, true, CancellationToken.None);
            else
                await RecordSuccessAsync(schedule, database, CancellationToken.None);
            return ToSummary(execution, true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            // A timed-out run is a recorded failure, not an exception: retrying it immediately would only time
            // out again while holding the database lock.
            execution.Fail($"Backup exceeded its timeout of {timeout.TotalSeconds:0} seconds and was stopped. " +
                           "Increase the schedule timeout for large databases.", Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await RecordFailureAsync(schedule, execution, database, false, CancellationToken.None);
            return ToSummary(execution, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.Fail(Redact(exception.Message, credentials), Now());
            await dbContext.SaveChangesAsync(CancellationToken.None);
            await RecordFailureAsync(schedule, execution, database, false, CancellationToken.None);
            return ToSummary(execution, true);
        }
    }

    /// <summary>
    ///     Sends one <c>backup.missing</c> alert per idle period for enabled schedules that have not produced a
    ///     successful backup in their configured number of days (upstream <c>CheckMissingDatabaseBackupsJob</c>).
    /// </summary>
    public async Task<int> CheckMissingBackupsAsync(CancellationToken cancellationToken = default)
    {
        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(MissingCheckLockKey,
            TimeSpan.FromMinutes(10), TimeSpan.Zero, cancellationToken);
        if (handle is null) return 0;
        DateTimeOffset now = Now();
        BackupSchedule[] candidates = dbContext.BackupSchedules
            .Where(schedule => schedule.Enabled && schedule.MissingAlertDays > 0)
            .ToArray();
        var sent = 0;
        foreach (BackupSchedule schedule in candidates.Where(schedule => schedule.IsBackupMissing(now)))
        {
            var databaseName = dbContext.DatabaseResources
                .Where(database => database.Id == schedule.DatabaseResourceId)
                .Select(database => database.Name)
                .AsEnumerable()
                .Select(name => name.Value)
                .FirstOrDefault();
            if (databaseName is null) continue;
            await notifications.PublishAsync(schedule.TeamId,
                OperationalNotifications.BackupMissing(schedule.Id.Value, schedule.DatabaseResourceId.Value,
                    databaseName, schedule.Name.Value, schedule.MissingAlertDays), cancellationToken);
            schedule.RecordMissingAlertSent(now);
            sent++;
        }

        if (sent > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return sent;
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private async Task<BackupExecutionSummary> SkipAsync(BackupExecution execution, DatabaseResource database,
        string reason, CancellationToken cancellationToken)
    {
        execution.Skip(reason, Now());
        await dbContext.SaveChangesAsync(CancellationToken.None);
        await auditWriter.RecordAsync(execution.TeamId, null, AuditActions.BackupSkipped,
            new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["databaseId"] = database.Id.Value.ToString("D") },
            cancellationToken);
        return ToSummary(execution, true);
    }

    private async Task RecordSuccessAsync(BackupSchedule? schedule, DatabaseResource database,
        CancellationToken cancellationToken)
    {
        if (schedule is null) return;
        var recovered = schedule.RecordSuccess(Now());
        await dbContext.SaveChangesAsync(cancellationToken);
        if (recovered)
            await notifications.PublishAsync(schedule.TeamId,
                OperationalNotifications.BackupRecovered(schedule.Id.Value, database.Id.Value, database.Name.Value,
                    schedule.Name.Value), cancellationToken);
    }

    private async Task RecordFailureAsync(BackupSchedule? schedule, BackupExecution execution,
        DatabaseResource database, bool stored, CancellationToken cancellationToken)
    {
        var notify = true;
        if (schedule is not null)
        {
            if (stored) schedule.RecordStored(Now());
            notify = schedule.RecordFailure(Now());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!stored)
            await auditWriter.RecordAsync(execution.TeamId, null, AuditActions.BackupFailed,
                new AuditTarget("backup", execution.Id.Value.ToString("D")), null,
                new Dictionary<string, object?> { ["databaseId"] = database.Id.Value.ToString("D") },
                cancellationToken);
        if (notify)
            await notifications.PublishAsync(execution.TeamId,
                OperationalNotifications.BackupFailed(execution.Id.Value, database.Id.Value, database.Name.Value),
                cancellationToken);
    }

    private static async Task UploadAsync(IObjectStorage target, string bucket, BackupArtifactFile artifact,
        DatabaseResource database, BackupExecution execution, CancellationToken cancellationToken)
    {
        await using Stream content = artifact.OpenRead();
        await target.PutAsync(new ObjectStoragePutRequest(
            new ObjectStorageKey(bucket, artifact.Key),
            content,
            artifact.ContentType,
            new Dictionary<string, string>
            {
                ["vessel.databaseId"] = database.Id.Value.ToString("D"),
                ["vessel.backupExecutionId"] = execution.Id.Value.ToString("D"),
                ["vessel.format"] = artifact.Format,
                ["vessel.sha256"] = artifact.Sha256
            },
            artifact.SizeBytes), cancellationToken);
    }

    private async Task<IReadOnlyList<string?>> ResolveDatabaseNamesAsync(BackupSchedule schedule,
        DatabaseResource database, CancellationToken cancellationToken)
    {
        BackupDatabaseSelection selection = schedule.Databases;
        switch (selection.Mode)
        {
            case BackupDatabaseSelectionMode.Named:
                return selection.Names.Cast<string?>().ToArray();
            case BackupDatabaseSelectionMode.All when database.Engine == DatabaseEngine.PostgreSql &&
                                                     database.LifecycleState == DatabaseLifecycleState.Running:
                try
                {
                    Server server = dbContext.Servers.Single(item => item.Id == database.ServerId);
                    var credentials = await secretVault.RevealForDeploymentAsync(schedule.TeamId,
                        database.CredentialsReferenceId, cancellationToken);
                    IReadOnlyList<string> names = await backupProvider.ListDatabasesAsync(
                        new DatabaseListRequest(database, ManagedRuntimeTargets.For(server), credentials),
                        cancellationToken);
                    string?[] valid = names
                        .Where(name => BackupDatabaseSelectionIsValid(name))
                        .Cast<string?>()
                        .ToArray();
                    return valid.Length > 0 ? valid : [null];
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The single default execution records the real error (or a skip) when it runs.
                    return [null];
                }
            default:
                return [null];
        }
    }

    private static bool BackupDatabaseSelectionIsValid(string name)
    {
        try
        {
            BackupDatabaseSelection.ValidateName(name);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    private BackupDestination[] LoadDestinations(BackupSchedule? schedule)
    {
        if (schedule is null || schedule.Destinations.Count == 0) return [];
        BackupDestinationId[] ids = schedule.DestinationIds.ToArray();
        return dbContext.BackupDestinations
            .Where(destination => destination.TeamId == schedule.TeamId && ids.Contains(destination.Id))
            .ToArray();
    }

    /// <summary>
    ///     Applies local and remote retention for the schedule (or, for manual backups, the default local count), per
    ///     logical database. Protected artifacts and artifacts with an active restore are never pruned.
    /// </summary>
    private async Task ApplyRetentionAsync(BackupSchedule? schedule, BackupExecution latest,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = Now();
        BackupScheduleId? scheduleId = schedule?.Id;
        var databaseName = latest.DatabaseName;
        BackupExecution[] candidates = dbContext.BackupExecutions
            .Where(execution => execution.TeamId == latest.TeamId
                                && execution.DatabaseResourceId == latest.DatabaseResourceId
                                && execution.ScheduleId == scheduleId
                                && execution.DatabaseName == databaseName
                                && !execution.Protected
                                && (execution.Status == BackupExecutionStatus.Succeeded
                                    || execution.Status == BackupExecutionStatus.RestoreSucceeded))
            .OrderByDescending(execution => execution.CreatedAt)
            .ToArray();
        HashSet<BackupExecutionId> restoring = dbContext.DatabaseRestores
            .Where(restore => restore.Status == DatabaseRestoreStatus.Queued ||
                              restore.Status == DatabaseRestoreStatus.Running)
            .Select(restore => restore.BackupExecutionId)
            .AsEnumerable()
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        candidates = candidates.Where(execution => !restoring.Contains(execution.Id)).ToArray();

        BackupRetentionPolicy local = schedule?.LocalRetention ??
                                      new BackupRetentionPolicy(DefaultManualRetentionCount, 0, 0);
        var pruned = 0;
        BackupExecution[] localCopies = candidates.Where(execution => execution.ArtifactBucket != null).ToArray();
        foreach (BackupExecution execution in local.SelectExpired(localCopies, item => item.CreatedAt,
                     item => item.SizeBytes ?? 0, now))
        {
            await storage.Local.Storage.DeleteAsync(
                new ObjectStorageKey(execution.ArtifactBucket!, execution.ArtifactKey!), cancellationToken);
            execution.MarkLocalCopyDeleted(now);
            pruned++;
        }

        if (schedule is not null)
            foreach (BackupDestination destination in LoadDestinations(schedule))
            {
                BackupExecution[] remoteCopies = candidates
                    .Where(execution => execution.Replicas.Any(replica =>
                        replica.BackupDestinationId == destination.Id && replica.IsAvailable))
                    .ToArray();
                IReadOnlyList<BackupExecution> expired = schedule.RemoteRetention.SelectExpired(remoteCopies,
                    item => item.CreatedAt, item => item.SizeBytes ?? 0, now);
                if (expired.Count == 0) continue;
                try
                {
                    using IBackupDestinationClient client = await connector.ConnectAsync(destination, cancellationToken);
                    foreach (BackupExecution execution in expired)
                    {
                        BackupArtifactReplica replica = execution.Replicas.First(item =>
                            item.BackupDestinationId == destination.Id && item.IsAvailable);
                        await client.DeleteAsync(new ObjectStorageKey(replica.Bucket, execution.ArtifactKey!),
                            cancellationToken);
                        execution.MarkReplicaDeleted(destination.Id, now);
                        pruned++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Remote pruning retries on the next successful run; local copies were already handled.
                }
            }

        if (pruned == 0) return;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.RecordAsync(latest.TeamId, null, AuditActions.BackupPruned,
            new AuditTarget("database", latest.DatabaseResourceId.Value.ToString("D")), null,
            new Dictionary<string, object?> { ["prunedCopies"] = pruned }, cancellationToken);
    }

    private async Task<int> DeleteCopiesAsync(BackupExecution execution, bool deleteRemote,
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
        foreach (BackupArtifactReplica replica in execution.Replicas.Where(replica => replica.IsAvailable).ToArray())
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

    private bool IsInFlight(BackupExecution execution)
    {
        return execution.Status is BackupExecutionStatus.Queued or BackupExecutionStatus.Running
                   or BackupExecutionStatus.RestoreValidated
               || dbContext.DatabaseRestores.Any(restore => restore.BackupExecutionId == execution.Id &&
                                                            (restore.Status == DatabaseRestoreStatus.Queued ||
                                                             restore.Status == DatabaseRestoreStatus.Running));
    }

    private void SyncRecurringJob(BackupSchedule schedule)
    {
        if (schedule.Enabled)
            recurringJobs.AddOrUpdate<RunBackupJob>(RecurringJobId(schedule.Id),
                job => job.RunAsync(schedule.Id.Value, CancellationToken.None), schedule.CronExpression);
        else
            recurringJobs.RemoveIfExists(RecurringJobId(schedule.Id));
    }

    private BackupDestinationId[] ResolveDestinations(TeamId teamId, IReadOnlyList<Guid>? ids)
    {
        if (ids is null || ids.Count == 0) return [];
        BackupDestinationId[] requested = ids.Distinct().Select(id => new BackupDestinationId(id)).ToArray();
        BackupDestinationId[] found = dbContext.BackupDestinations
            .Where(destination => destination.TeamId == teamId && requested.Contains(destination.Id))
            .Select(destination => destination.Id)
            .ToArray();
        // Unknown and other teams' destinations are reported the same way, so IDs cannot be probed.
        if (found.Length != requested.Length) throw new DomainException("Backup destination was not found.");
        return requested;
    }

    private static BackupDatabaseSelection ToSelection(bool dumpAll, IReadOnlyList<string>? databases)
    {
        if (databases is { Count: > 0 }) return BackupDatabaseSelection.Named(databases);
        return dumpAll ? BackupDatabaseSelection.All : BackupDatabaseSelection.Default;
    }

    private static BackupRetentionPolicy ToRetention(BackupRetentionRequest? request, int defaultCount)
    {
        if (request is null) return new BackupRetentionPolicy(defaultCount, 0, 0);
        if (request.MaxStorageGigabytes is < 0 or > 1_000_000)
            throw new DomainException("Backup retention size must be between 0 and 1,000,000 GB.");
        var maxBytes = (long)decimal.Round(request.MaxStorageGigabytes * 1024m * 1024m * 1024m);
        return new BackupRetentionPolicy(request.Count, request.Days, maxBytes);
    }

    private async Task<BackupSchedule> GetScheduleAsync(UserId actorUserId, TeamId teamId,
        BackupScheduleId scheduleId, string permission, CancellationToken cancellationToken)
    {
        BackupSchedule schedule = await dbContext.BackupScheduleRepository.GetByIdAsync(scheduleId, cancellationToken)
                                  ?? throw new InvalidOperationException("Backup schedule was not found.");
        if (schedule.TeamId != teamId) throw new UnauthorizedAccessException("Backup schedule is outside the active team.");
        authorization.Demand(actorUserId, teamId, ResourceRef.Database(schedule.DatabaseResourceId), permission);
        return schedule;
    }

    private async Task<BackupExecution> GetExecutionAsync(TeamId teamId, BackupExecutionId executionId,
        CancellationToken cancellationToken)
    {
        BackupExecution execution =
            await dbContext.BackupExecutionRepository.GetByIdAsync(executionId, cancellationToken)
            ?? throw new InvalidOperationException("Backup execution was not found.");
        if (execution.TeamId != teamId) throw new UnauthorizedAccessException("Backup is outside the active team.");
        return execution;
    }

    private async Task<DatabaseResource> GetDatabaseAsync(DatabaseResourceId databaseId,
        CancellationToken cancellationToken)
    {
        return await dbContext.DatabaseResourceRepository.GetByIdAsync(databaseId, cancellationToken)
               ?? throw new InvalidOperationException("Database was not found.");
    }

    private string Redact(string message, string? credentials)
    {
        return credentials is null
            ? redactor.Redact(message)
            : redactor.Redact(message, new RedactionContext([credentials]));
    }

    private DateTimeOffset Now()
    {
        return timeProvider.GetUtcNow();
    }

    internal static BackupScheduleSummary ToSummary(BackupSchedule schedule)
    {
        BackupDatabaseSelection selection = schedule.Databases;
        return new BackupScheduleSummary(schedule.Id.Value, schedule.DatabaseResourceId.Value, schedule.Name.Value,
            schedule.CronExpression, schedule.RetentionCount, schedule.StorageKind, schedule.Enabled,
            schedule.LastRunAt, schedule.TimeoutSeconds, selection.Mode, selection.Names, schedule.DumpFormat,
            new BackupRetentionSummary(schedule.RetentionCount, schedule.LocalRetentionDays,
                schedule.LocalRetentionMaxBytes),
            new BackupRetentionSummary(schedule.RemoteRetentionCount, schedule.RemoteRetentionDays,
                schedule.RemoteRetentionMaxBytes),
            schedule.DestinationIds.Select(id => id.Value).ToArray(), schedule.DisableLocalCopy,
            schedule.MissingAlertDays, schedule.LastSucceededAt, schedule.FailingSince);
    }

    /// <summary>
    ///     Maps an execution. Without <paramref name="includeSensitive" /> the failure/skip reason and replica messages
    ///     are hidden (they can carry tool output); <see cref="BackupExecutionSummary.MessageHidden" /> is set instead.
    /// </summary>
    public static BackupExecutionSummary ToSummary(BackupExecution execution, bool includeSensitive)
    {
        var hasMessage = execution.FailureReason is not null || execution.LastRestoreFailureReason is not null ||
                         execution.Replicas.Any(replica => replica.Message is not null);
        return new BackupExecutionSummary(execution.Id.Value, execution.DatabaseResourceId.Value,
            execution.ScheduleId?.Value, execution.Status, execution.StorageKind, execution.ArtifactBucket,
            execution.ArtifactKey, execution.SizeBytes, execution.ChecksumSha256, execution.Protected,
            execution.CreatedAt, execution.FinishedAt,
            includeSensitive ? execution.FailureReason : null,
            execution.LastRestoreFailedAt,
            includeSensitive ? execution.LastRestoreFailureReason : null,
            execution.DatabaseName,
            execution.ArtifactFormat,
            execution.HasLocalCopy,
            execution.Replicas.Select(replica => new BackupReplicaSummary(replica.BackupDestinationId?.Value,
                replica.Bucket, replica.Uploaded, replica.DeletedAt is not null,
                includeSensitive ? replica.Message : null, replica.CreatedAt)).ToArray(),
            !includeSensitive && hasMessage);
    }
}

public sealed class CheckMissingBackupsJob(DatabaseBackupService service)
{
    public const string RecurringJobId = "backups:missing-check";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return service.CheckMissingBackupsAsync(cancellationToken);
    }
}

public static class BackupRecurringJobs
{
    public static void Register(IRecurringJobScheduler scheduler)
    {
        scheduler.AddOrUpdate<CheckMissingBackupsJob>(CheckMissingBackupsJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "0 * * * *");
        scheduler.AddOrUpdate<RevalidateBackupDestinationsJob>(RevalidateBackupDestinationsJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "30 * * * *");
        // DATA-09 volume backups and DATA-10 import uploads.
        scheduler.AddOrUpdate<Volumes.VolumeBackupRecoveryJob>(Volumes.VolumeBackupRecoveryJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "*/5 * * * *");
        scheduler.AddOrUpdate<Volumes.CheckMissingVolumeBackupsJob>(Volumes.CheckMissingVolumeBackupsJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "15 * * * *");
        scheduler.AddOrUpdate<ManagedServices.DatabaseImports.CleanupDatabaseImportUploadsJob>(
            ManagedServices.DatabaseImports.CleanupDatabaseImportUploadsJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None), "45 * * * *");
    }
}
